using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SimpleLyrics.Core.Audio;

/// <summary>播放状态。</summary>
public enum AudioPlaybackState
{
    /// <summary>未在播放。位置停在最后一次定位处。</summary>
    Stopped,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>已暂停，位置冻结。</summary>
    Paused,
}

/// <summary>
/// 音频播放器：后台增量解码进 <see cref="PcmBlockStore"/>，前台由 NAudio 送到声卡。
/// </summary>
/// <remarks>
/// <para>
/// <b>核心约束是「音频回调线程绝不能等待解码」</b>。这里的做法不是预取，而是把问题挪走：
/// 调用方在按下播放之前必须先让 <see cref="Prepare"/> 把缓存铺到起点，
/// 于是回调线程读到的永远是已经在内存里的数据。解码只是「提前铺路」，
/// 它比播放快三个数量级，正常情况永远追得上。
/// </para>
/// <para>
/// <b>seek 的处理</b>：目标在缓存区间之外时，本类不做任何等待——由调用方先
/// <see cref="Halt"/> 停下播放、再 <see cref="Prepare"/>，铺好之后重新 <see cref="Play"/>。
/// 由于此时播放已停止，重新解码的耗时不影响任何实时性约束，
/// 所以缓存可以放心大胆地丢弃重来。
/// </para>
/// <para>
/// <b>反向定位只能从头重解</b>：FFmpeg 不能倒着解码。目标落在缓存起点之前时，
/// 唯一的办法是丢掉整段缓存、关掉解码器重开、从 0 解到目标。
/// 只要缓存始终从 0 开始（内存够用时的常态），这条路径就不会被触发。
/// </para>
/// <para><b>线程模型</b>：一个解码线程、一个音频回调线程，加上任意外部线程调用公开方法。</para>
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    /// <summary>一次增量解码产出的帧数上限。太小会让锁竞争变频繁，太大则响应目标变化变迟钝。</summary>
    private const int ChunkFrames = 16384;

    /// <summary>预解码余量的上限（秒）。实际值还会被内存预算压低，见 <see cref="LookaheadShare"/>。</summary>
    private const double LookaheadSeconds = 90;

    /// <summary>丢弃缓存时保留下来的历史上限（秒）。</summary>
    private const double RetentionSeconds = 120;

    /// <summary>预算里划给「向前预解码」的比例。</summary>
    private const double LookaheadShare = 0.5;

    /// <summary>预算里划给「保留已播过的历史」的比例。</summary>
    /// <remarks>
    /// 这两个比例之和必须明显小于 1，否则预算会被两头同时占满，
    /// 缓存再也腾不出空间往前推进——播到缓存末尾就卡住了。
    /// 预算被压得很小时这一点尤其致命：固定 120 秒的保留量在 4 MB 预算下是装不下的，
    /// 于是「保留历史」这条善意规则会把整个预算吃光。
    /// </remarks>
    private const double RetentionShare = 0.25;

    /// <summary>缓存暂时没跟上时的等待上限。超过它就返回 0，让播放自然停下而不是把线程挂死。</summary>
    private const int CacheWaitMilliseconds = 250;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly PcmBlockStore _store;
    private readonly StoreSink _sink;
    private readonly Thread _decodeThread;
    private readonly PlaybackClock _clock;
    private readonly long _totalFrames;

    /// <summary>解码线程独占；重定位时会被销毁重建。</summary>
    private FfmpegDecoder? _decoder;

    /// <summary>解码线程独占。是否已读到文件末尾。</summary>
    private volatile bool _eof;

    private long _targetFrame;

    /// <summary>音频回调当前的读取位置。</summary>
    private long _playFrame;

    /// <summary>
    /// 最近一次被关注的位置：播放位置，或用户滚动到的地方。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="_playFrame"/> 分开，是因为暂停状态下用户滚动并不会改变播放位置，
    /// 但缓存丢弃的保留边界必须跟着用户跑，否则滚到远处时会因为「坚持保留旧位置附近的历史」
    /// 而腾不出空间，解码就此卡死。
    /// </remarks>
    private long _focusFrame;

    private double _pausedSeconds;
    private long _budgetBytes;
    private readonly long? _budgetOverride;
    private volatile bool _disposed;
    private volatile bool _workPending;
    private volatile AudioPlaybackState _state;

    private WaveOut? _output;
    private IWaveProvider? _provider;

    /// <summary>
    /// 创建一个播放器并立即打开解码器。
    /// </summary>
    /// <param name="path">音频文件路径。</param>
    /// <param name="totalFrames">
    /// 权威总帧数，取自完整解码得到的 <c>PeakPyramid.TotalFrames</c>。
    /// 不能从容器时长换算——ADTS AAC 的容器声明实测能偏二十多秒。
    /// </param>
    /// <param name="cacheBudget">
    /// 覆盖缓存预算（字节）。为 null 时按系统可用内存自动决定。
    /// 显式指定主要是给探针用的——只有把预算压到很小，才能在几分钟的素材上触发
    /// 「丢弃头部」和「反向重定位」这两条平时走不到的路径。
    /// </param>
    public AudioPlayer(string path, long totalFrames, long? cacheBudget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalFrames);

        _path = path;
        _totalFrames = totalFrames;
        _budgetOverride = cacheBudget;

        _decoder = FfmpegDecoder.Open(path, 2);
        Format = _decoder.OutputFormat;

        _store = new PcmBlockStore(Format.SampleRate, Format.Channels);
        _sink = new StoreSink(this);
        _clock = new PlaybackClock(DeviceFrames, Format.SampleRate);

        // 先探一次，让解码线程一上来就知道能铺多远。
        _budgetBytes = cacheBudget ?? SystemMemory.CacheBudget();

        _decodeThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "SimpleLyrics.Decode",
            // 解码只是「提前铺路」，没有实时性要求，不该和界面抢 CPU。
            Priority = ThreadPriority.BelowNormal,
        };
        _decodeThread.Start();
    }

    /// <summary>解码输出格式。采样率跟随源文件，声道数固定为 2。</summary>
    public PcmFormat Format { get; }

    /// <summary>整曲时长，由权威总帧数得出。</summary>
    public double DurationSeconds => (double)_totalFrames / Format.SampleRate;

    /// <summary>当前状态。</summary>
    public AudioPlaybackState State => _state;

    /// <summary>缓存区间起始时刻（秒）。</summary>
    public double CacheStartSeconds
    {
        get { lock (_gate) return _store.FirstSeconds; }
    }

    /// <summary>缓存区间结束时刻（秒）。</summary>
    public double CacheEndSeconds
    {
        get { lock (_gate) return _store.EndSeconds; }
    }

    /// <summary>缓存是否已经建立（至少解出过一块）。</summary>
    public bool HasCache
    {
        get { lock (_gate) return _store.EndFrame > 0; }
    }

    /// <summary>解码过程中发生的异常；为 null 表示正常。</summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// 当前播放位置（秒）。
    /// </summary>
    /// <remarks>
    /// 播放中来自设备时钟——这是唯一可信的位置来源。用 <c>Stopwatch</c> 自己计时会随
    /// 声卡时钟漂移累积误差，几分钟就能偏出打轴能接受的范围。
    /// </remarks>
    public double PositionSeconds => _state == AudioPlaybackState.Playing ? _clock.Value : _pausedSeconds;

    /// <summary>缓存区间发生变化。在解码线程上触发，订阅方需自行切回界面线程。</summary>
    public event EventHandler? CacheChanged;

    /// <summary>自然播放到末尾。在音频回调线程上触发，订阅方需自行切回界面线程。</summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>播放状态发生变化。</summary>
    public event EventHandler? StateChanged;

    /// <summary>某个时刻是否可以立即开始播放。</summary>
    /// <remarks>
    /// <para>
    /// <b>音频之外恒为 true。</b>超限滚动会把播放头放到 0 秒之前或曲尾之后，
    /// 那里根本没有音频，因此天然就是静音，不需要任何缓存来支撑。
    /// </para>
    /// <para>
    /// 这一点必须与「缓存没铺到」严格区分开：后者只是还没准备好，等一等就行；
    /// 前者若也返回 false，播放按钮就会被一个<b>永远无法满足的条件</b>永久禁用——
    /// 用户把指示条拖到曲前就再也放不出声音了。
    /// </para>
    /// </remarks>
    public bool Covers(double seconds)
    {
        if (seconds < 0 || seconds >= DurationSeconds) return true;

        lock (_gate) return _store.Covers(seconds);
    }

    /// <summary>
    /// 诊断用：直接从缓存读一段 PCM，不走播放路径。
    /// </summary>
    /// <remarks>
    /// 供探针核对「分块增量解码出来的样本」与「一次性解码出来的样本」是否逐位相同。
    /// 分块解码真正容易出错的地方就在这儿——重采样器跨块的状态、块边界的切分，
    /// 任何一处算错都会让播出来的声音与画出来的波形对不上。
    /// </remarks>
    internal int ReadCached(long frameIndex, Span<float> destination, int frameCount)
    {
        lock (_gate) return _store.Read(frameIndex, destination, frameCount);
    }

    /// <summary>
    /// 请求把缓存铺到 <paramref name="seconds"/> 处，并尽量向后多解一段。
    /// </summary>
    /// <remarks>
    /// 这是异步的：方法立即返回，解码在后台线程上进行，进度通过 <see cref="CacheChanged"/> 上报。
    /// 反复调用是安全的，后一次会覆盖前一次的目标——快速滚动因此不会堆积出一串解码任务，
    /// 只会让当前这一趟提前收工转向新目标。
    /// </remarks>
    public void Prepare(double seconds)
    {
        if (_disposed) return;

        // 音频之外没有东西可铺，把关注点收到边界上，从这里往后铺即可。
        var frame = Math.Clamp(SecondsToFrame(seconds), 0, _totalFrames);

        lock (_gate)
        {
            // 预解码量不能超过预算划给它的那一份。若照搬固定的 90 秒，
            // 小预算下就会为了「多解一点」而把预算撑爆好几倍。
            var budgetFrames = Math.Max(1, _budgetBytes / Format.BlockAlign);
            var lookaheadFrames = Math.Min(
                (long)(LookaheadSeconds * Format.SampleRate),
                (long)(budgetFrames * LookaheadShare));

            _focusFrame = frame;
            _targetFrame = Math.Min(_totalFrames, frame + lookaheadFrames);

            // 总是叫醒解码线程，由它自己判断还需不需要干活。
            // 这样「目标没变但内存腾出余量了」这种情况也能继续往前铺。
            _workPending = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// 从指定位置开始播放。
    /// </summary>
    /// <remarks>
    /// <b>允许落在音频之外。</b>超限滚动会把播放头放到 0 秒之前或曲尾之后，
    /// 那两段都按静音正常播放——位置照常前进，走到 0 秒时自然接上音频，
    /// 走到曲尾之外的那段静音末尾时才算结束。
    /// </remarks>
    public void Play(double seconds)
    {
        if (_disposed || !double.IsFinite(seconds)) return;

        var frame = SecondsToFrame(seconds);

        EnsureOutput();

        lock (_gate)
        {
            _playFrame = frame;

            // 缓存丢弃的保留边界必须落在音频之内，负位置或曲尾之外对它没有意义。
            _focusFrame = Math.Clamp(frame, 0, _totalFrames);
        }

        // 先 Stop 再 Play：waveOutReset 会丢弃设备缓冲里已经排着的旧内容，
        // 并让 GetPosition() 归零，于是位置换算不需要再补偿任何残留量。
        _output!.Stop();

        _clock.Rebase(seconds);
        _pausedSeconds = seconds;
        _output.Play();

        SetState(AudioPlaybackState.Playing);
    }

    /// <summary>暂停，位置冻结在当前处。</summary>
    public void Pause()
    {
        if (_state != AudioPlaybackState.Playing) return;

        _pausedSeconds = _clock.Value;
        _output?.Pause();
        SetState(AudioPlaybackState.Paused);
    }

    /// <summary>从暂停处继续。</summary>
    public void Resume()
    {
        if (_state != AudioPlaybackState.Paused) return;

        // 暂停期间设备位置可能冻结也可能继续走，重新锚定一次就与驱动行为无关了。
        _clock.Rebase(_pausedSeconds);

        // 从暂停恢复也用 Play()：NAudio 的 WaveOut 在 Paused 状态下调用它就是继续，
        // 而不是从头开始——真正的「从头」是靠 Stop() 把设备位置清零实现的。
        _output?.Play();
        SetState(AudioPlaybackState.Playing);
    }

    /// <summary>停止播放并定位到指定位置。用于「目标在缓存之外」时先刹车。</summary>
    public void Halt(double seconds)
    {
        if (!double.IsFinite(seconds)) return;

        var frame = SecondsToFrame(seconds);

        _output?.Stop();

        lock (_gate)
        {
            _playFrame = frame;
            _focusFrame = Math.Clamp(frame, 0, _totalFrames);
        }

        _pausedSeconds = seconds;

        SetState(AudioPlaybackState.Stopped);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _provider = null;

        // 解码线程可能正卡在 DecodeChunk 里，等它做完当前这一小块自己退出来。
        if (!_decodeThread.Join(TimeSpan.FromSeconds(5)))
        {
            // 没有强杀线程的手段，也不该有。泄漏一个后台线程好过破坏 FFmpeg 的堆状态。
            Debug.WriteLine("解码线程未能在超时内退出。");
        }

        _decoder?.Dispose();
        _decoder = null;
    }

    // ------------------------------------------------------------ 解码线程

    private void DecodeLoop()
    {
        while (true)
        {
            lock (_gate)
            {
                while (!_disposed && !_workPending) Monitor.Wait(_gate);
                if (_disposed) return;
                _workPending = false;
            }

            try
            {
                Pump();
            }
            catch (Exception ex)
            {
                Failure = ex;
                _eof = true;
                RaiseCacheChanged();
            }
        }
    }

    /// <summary>把缓存铺到目标位置，之后只要还有内存余量就继续往前铺。</summary>
    private void Pump()
    {
        lock (_gate)
        {
            // 目标已经覆盖、内存也还有余量可花，就没有继续的理由。
            if (_store.EndFrame >= _targetFrame && _store.ApproximateBytes >= _budgetBytes) return;

            // 目标落在缓存起点之前：没法倒着解码，只能整段丢掉重来。
            if (_store.EndFrame > 0 && _targetFrame < _store.FirstFrame)
            {
                _decoder?.Dispose();
                _decoder = null;
                _store.Clear();
                _eof = false;
            }
        }

        while (true)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_eof) break;

                // 目标必须覆盖到；覆盖之后，只要还有内存余量就继续往前铺。
                // 这正是「更积极地缓存整曲」的落点：短音频一趟就全进来了，
                // 长音频铺到预算用尽为止，而不是停在起点往后一点点就不管了。
                if (_store.EndFrame >= _targetFrame && _store.ApproximateBytes >= _budgetBytes) break;
            }

            TrimToBudget();

            var decoder = EnsureDecoder();
            if (decoder is null) break;

            long produced;
            try
            {
                produced = decoder.DecodeChunk(_sink, ChunkFrames);
            }
            catch (Exception ex)
            {
                Failure = ex;
                _eof = true;
                break;
            }

            if (produced == 0)
            {
                _eof = true;
                break;
            }

            RaiseCacheChanged();
        }

        RaiseCacheChanged();
    }

    private FfmpegDecoder? EnsureDecoder()
    {
        if (_decoder is not null) return _decoder;

        try
        {
            _decoder = FfmpegDecoder.Open(_path, Format.Channels);
            return _decoder;
        }
        catch (Exception ex)
        {
            Failure = ex;
            _eof = true;
            return null;
        }
    }

    /// <summary>
    /// 按内存预算裁剪缓存，从最早的一端丢起。
    /// </summary>
    /// <remarks>
    /// 预算只在快用完时才重新探测——每解一小块就去问一次系统，既浪费又让上限抖得厉害。
    /// 丢弃时至少保留最近 <see cref="RetentionSeconds"/> 秒的历史：
    /// 打轴时反复回退重听是常态操作，把这些内容丢掉等于让用户每次都等一次重新解码。
    /// </remarks>
    private void TrimToBudget()
    {
        lock (_gate)
        {
            if (_store.ApproximateBytes < _budgetBytes) return;

            _budgetBytes = _budgetOverride ?? SystemMemory.CacheBudget();
            if (_store.ApproximateBytes <= _budgetBytes) return;

            // 保留历史同样受预算约束：固定 120 秒在极小预算下会把整个缓存撑满。
            var budgetFrames = Math.Max(1, _budgetBytes / Format.BlockAlign);
            var retentionFrames = Math.Min(
                (long)(RetentionSeconds * Format.SampleRate),
                (long)(budgetFrames * RetentionShare));

            var keepFrom = _focusFrame - retentionFrames;
            var dropTo = Math.Min(keepFrom, _store.EndFrame - PcmBlockStore.BlockFrames);

            if (dropTo > _store.FirstFrame) _store.DropBefore(dropTo);
        }
    }

    private void RaiseCacheChanged() => CacheChanged?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------ 音频输出

    private void EnsureOutput()
    {
        if (_output is not null) return;

        _provider = new PcmProvider(this);
        _output = new WaveOut
        {
            // 每个缓冲 40 毫秒、共 3 个，总延迟约 120 毫秒。
            // 缓冲刻意取小：seek 时 Stop 丢掉的内容越少，听感越干脆。
            // 代价是回调更频繁，但这里只是从内存里拷数据，不构成负担。
            BufferMilliseconds = 40,
            NumberOfBuffers = 3,
        };
        _output.Init(_provider);
    }

    /// <summary>设备已经播出的帧数。</summary>
    private long DeviceFrames()
    {
        var output = _output;
        return output is null ? 0 : output.GetPosition() / Format.BlockAlign;
    }

    private void SetState(AudioPlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 音频之后的静音区长度。
    /// </summary>
    /// <remarks>
    /// 超限滚动允许把播放头放到曲尾之外最多一个整曲时长处，那一段同样按静音处理，
    /// 播完才算结束。这样「把指示条放到末尾之外再按播放」不会立刻就停，
    /// 与曲首之前的行为对称——两边都是静音，只是内容不同。
    /// </remarks>
    private long SilenceTailFrames => _totalFrames * 2;

    /// <summary>
    /// 从某个帧位置开始，播放路径上会连续产出多少帧静音。
    /// </summary>
    /// <remarks>
    /// 抽成静态纯函数是为了让探针能直接验证音频之外的静音区间——
    /// 完整的播放路径要打开音频设备，沙箱里跑不起来。
    /// </remarks>
    internal static int SilenceFramesAt(long frameIndex, int maxFrames, long totalFrames)
    {
        if (maxFrames <= 0 || totalFrames <= 0) return 0;

        // 曲首之前
        if (frameIndex < 0) return (int)Math.Min(maxFrames, -frameIndex);

        // 曲尾之后，一直到超限区域的上界
        if (frameIndex >= totalFrames)
        {
            return (int)Math.Clamp(totalFrames * 2 - frameIndex, 0, maxFrames);
        }

        return 0;
    }

    /// <summary>音频回调：从缓存取数，音频之外产出静音。真走不通时返回 0，让播放停下而不是把回调线程挂住。</summary>
    private int ReadPlayback(Span<byte> buffer, int frameCount)
    {
        var blockAlign = Format.BlockAlign;
        var channels = Format.Channels;
        var destination = MemoryMarshal.Cast<byte, float>(buffer[..(frameCount * blockAlign)]);
        var tailLimit = SilenceTailFrames;

        while (true)
        {
            long frame;
            int produced;

            lock (_gate)
            {
                frame = _playFrame;

                var silence = SilenceFramesAt(frame, frameCount, _totalFrames);

                if (silence > 0)
                {
                    // 曲首之前或曲尾之后的静音。位置照常前进，走到音频范围内时
                    // 下一次调用自然从缓存取到音频，中间不需要任何切换动作，也就不会有接缝。
                    destination[..(silence * channels)].Clear();
                    _playFrame = frame + silence;
                    produced = silence;
                }
                else
                {
                    produced = _store.Read(frame, destination, frameCount);

                    if (produced > 0)
                    {
                        _playFrame = frame + produced;

                        // 丢弃边界要跟着播放前进，否则长时间播放会把内存顶满。
                        _focusFrame = _playFrame;
                    }
                }
            }

            if (produced > 0) return produced * blockAlign;

            // 走到这里只剩两种可能：尾部静音也播完了，或者音频之内缓存暂时没跟上。
            if (frame >= tailLimit || (_eof && !HasMoreCached(frame)))
            {
                NotifyCompleted();
                return 0;
            }

            lock (_gate) Monitor.Wait(_gate, CacheWaitMilliseconds);
        }
    }

    private bool HasMoreCached(long frame)
    {
        lock (_gate) return frame < _store.EndFrame;
    }

    private void NotifyCompleted()
    {
        if (_state != AudioPlaybackState.Playing) return;

        long frame;
        lock (_gate) frame = _playFrame;

        // 停在真正播到的位置，而不是一律写回曲尾：从曲尾之外的静音区起播时，
        // 结束位置本来就在音频之后，写回曲尾会让指示条莫名跳回去。
        _pausedSeconds = (double)frame / Format.SampleRate;

        SetState(AudioPlaybackState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把时刻换算成帧号。
    /// </summary>
    /// <remarks>
    /// <b>刻意不钳制到音频范围内</b>：超限滚动时播放头本来就可以落在音频之外，
    /// 那是合法的静音区间而不是错误。这里只防越界到 <see cref="long"/> 溢出。
    /// </remarks>
    private long SecondsToFrame(double seconds)
    {
        if (!double.IsFinite(seconds)) return 0;

        var frame = seconds * Format.SampleRate;
        if (frame >= long.MaxValue) return long.MaxValue;
        if (frame <= long.MinValue) return long.MinValue;

        return (long)Math.Round(frame);
    }

    // ------------------------------------------------------------ 内部类型

    /// <summary>把解码输出写进分块存储。</summary>
    private sealed class StoreSink(AudioPlayer owner) : IPcmSink
    {
        public void Write(ReadOnlySpan<float> samples, int frameCount)
        {
            lock (owner._gate)
            {
                owner._store.Append(samples, frameCount);

                // 叫醒可能正在等数据的音频回调线程。
                Monitor.PulseAll(owner._gate);
            }
        }
    }

    /// <summary>NAudio 的取数口。只做搬运，不做任何等待解码的决策。</summary>
    private sealed class PcmProvider(AudioPlayer owner) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(owner.Format.SampleRate, owner.Format.Channels);

        public int Read(Span<byte> buffer)
        {
            var frameCount = buffer.Length / owner.Format.BlockAlign;
            return frameCount <= 0 ? 0 : owner.ReadPlayback(buffer, frameCount);
        }
    }

    /// <summary>
    /// 播放位置时钟：以设备报告的已播帧数为锚，用高精度计时器在两次上报之间插值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 设备位置通常每几十毫秒才跳一次（等于一个缓冲区的时长），直接拿它驱动界面，
    /// 波形会一格格地顿。插值把这个阶梯抹平。
    /// </para>
    /// <para>
    /// 插值量封顶 <see cref="MaxInterpolationSeconds"/>：设备长时间不推进（暂停、卡住）时，
    /// 位置会停在「锚点 + 上限」处不动，而不是一路飞出去。
    /// </para>
    /// <para>
    /// 另有单调性钳制。重新锚定或设备位置回退时原始值会短暂倒退，
    /// 让波形倒着走比让它停一下难看得多。
    /// </para>
    /// </remarks>
    private sealed class PlaybackClock(Func<long> deviceFrames, int sampleRate)
    {
        private const double MaxInterpolationSeconds = 0.12;

        private long _anchorFrames;
        private long _anchorTicks;
        private double _originSeconds;
        private double _lastSeconds;

        /// <summary>把时钟锚定到指定位置，之后从这个值开始推进。</summary>
        public void Rebase(double seconds)
        {
            var frames = deviceFrames();

            _originSeconds = seconds - (double)frames / sampleRate;
            _anchorFrames = frames;
            _anchorTicks = Stopwatch.GetTimestamp();
            _lastSeconds = seconds;
        }

        public double Value
        {
            get
            {
                var frames = deviceFrames();
                var ticks = Stopwatch.GetTimestamp();

                if (frames != _anchorFrames)
                {
                    _anchorFrames = frames;
                    _anchorTicks = ticks;
                }

                var sinceAnchor = (ticks - _anchorTicks) / (double)Stopwatch.Frequency;
                var value = _originSeconds
                          + (double)frames / sampleRate
                          + Math.Min(sinceAnchor, MaxInterpolationSeconds);

                if (value < _lastSeconds) value = _lastSeconds;
                _lastSeconds = value;

                return value;
            }
        }
    }
}
