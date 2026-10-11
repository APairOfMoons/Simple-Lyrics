using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleLyrics.Core.Audio;
using SimpleLyrics.Core.Peaks;

namespace SimpleLyrics.App.ViewModels;

/// <summary>
/// 右栏「文件信息」里的一行。
/// </summary>
/// <param name="IsLyrics">
/// 这一行描述的是歌词而不是音频。载入新音频时整块 <c>Details</c> 都会被清空重建，
/// 歌词却是另一条独立的导入路径，重建后要靠这个标志把歌词那几行放回去。
/// </param>
public sealed record InfoRow(string Label, string Value, bool IsLyrics = false);

/// <summary>
/// 主窗口的视图模型。
/// </summary>
/// <remarks>
/// <para>
/// 界面上的「播放头位置」由 <see cref="PlayStartSeconds"/> 表达——它由波形控件算出
/// （顶端时刻加上指示器比例对应的那一段），因为这个换算需要控件高度，
/// 视图模型拿不到。于是数据流是：控件报告位置 → 这里决定要不要铺缓存或跳转。
/// </para>
/// <para>
/// 播放过程中位置是反着流的：这里的 <see cref="PlayheadSeconds"/> 被设备时钟持续刷新，
/// 控件据此滚动波形。两者都写同一个属性，靠 <see cref="_suppressPlayhead"/> 区分
/// 「我方刷新」与「用户挪动了播放头」。
/// </para>
/// <para>
/// 对话框之类依赖平台能力的交互留在 View 里，视图模型只处理状态。
/// </para>
/// <para>
/// <b>本类分两个文件放。</b>这里是音频与播放（解码、设备时钟、播放缓存、文件信息），
/// <c>MainWindowViewModel.Lyrics.cs</c> 是歌词那一份文档（载入、编辑、撤销、保存）。
/// 两块状态几乎不相干，只有「换音频要清掉歌词」一处交界，而那处是一个方法调用。
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 手动改变播放位置之后，等这么久才真正开始铺缓存。
    /// </summary>
    /// <remarks>
    /// 拖动指示器和滚动波形都是连续动作，每一帧都会产生一个新位置。若立刻响应，
    /// 就会排出一串半途而废的解码任务，既浪费解码又让界面卡顿。
    /// 等手停下来再动手，实际只解用户最终停留的那一处。
    /// </remarks>
    private static readonly TimeSpan PrepareDebounce = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 播放位置的刷新间隔。
    /// </summary>
    /// <remarks>
    /// 取 8 毫秒（约 120 Hz）而不是贴着 60 Hz 的 16 毫秒：定时器节拍与渲染节拍之间
    /// 总会有些漂移，卡在同一个频率上就会周期性错过某一帧。刷新得比渲染快一档，
    /// 每一帧就都能拿到最新位置。真正的重绘仍由 Avalonia 按自己的帧率决定，
    /// 且控件里有半像素死区挡着，提高刷新率并不会成比例地增加绘制量。
    /// </remarks>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(8);

    private readonly DispatcherTimer _prepareTimer;
    private readonly DispatcherTimer _tickTimer;

    private AudioPlayer? _player;
    private string? _audioPath;
    private long _totalFrames;
    private double _pendingPrepareSeconds;
    private bool _suppressPlayhead;

    /// <summary>
    /// 播放头被用户挪到过别处。
    /// </summary>
    /// <remarks>
    /// 用来区分「原地点播放」与「挪了位置再点播放」：前者可以从暂停处平滑继续
    /// （不重置设备缓冲，听不到咔嗒），后者必须按新位置重新定位，
    /// 否则用户滚动波形看到的位移等于白做——一按播放就跳回原位。
    /// </remarks>
    private bool _playheadRelocated;

    private bool _disposed;

    public MainWindowViewModel()
    {
        _prepareTimer = new DispatcherTimer { Interval = PrepareDebounce };
        _prepareTimer.Tick += OnPrepareTimerTick;

        // 用 Render 优先级让位置更新紧贴渲染节拍。默认的 Normal 与渲染之间没有对齐关系，
        // 会出现某一帧更新两次、下一帧一次也没有——看上去就是波形一抽一抽的。
        _tickTimer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, OnTickTimerTick);
    }

    // ------------------------------------------------------------ 文件

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudio))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private PeakPyramid? _peaks;

    [ObservableProperty]
    private string _fileName = "未载入音频";

    [ObservableProperty]
    private string _statusText = "就绪 —— 点击「打开音频…」或按 Ctrl+O";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    /// <summary>波形滚动位置（秒）。与 <c>WaveformView</c> 双向绑定。</summary>
    [ObservableProperty]
    private double _scrollSeconds;

    /// <summary>波形纵向缩放（每秒钟占多少像素）。与 <c>WaveformView</c> 双向绑定。</summary>
    [ObservableProperty]
    private double _pixelsPerSecond = 40;

    /// <summary>右栏展示的文件信息。</summary>
    public ObservableCollection<InfoRow> Details { get; } = [];

    // ------------------------------------------------------------ 播放

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playButtonText = "播放";

    /// <summary>
    /// 播放头所在时刻（秒）。<see cref="double.NaN"/> 表示尚未开始播放。
    /// </summary>
    [ObservableProperty]
    private double _playheadSeconds = double.NaN;

    /// <summary>波形控件算出的播放头时刻。它是缓存的准备目标，也是播放的起点。</summary>
    [ObservableProperty]
    private double _playStartSeconds;

    /// <summary>已解码缓存区间的起始时刻（秒）。</summary>
    [ObservableProperty]
    private double _cacheStartSeconds = double.NaN;

    /// <summary>已解码缓存区间的结束时刻（秒）。</summary>
    [ObservableProperty]
    private double _cacheEndSeconds = double.NaN;

    /// <summary>播放头还没铺好缓存。此时播放按钮应当禁用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private bool _isPreparing;

    [ObservableProperty]
    private string _positionText = "—";

    public bool HasAudio => Peaks is not null;

    public string DurationText => Peaks is null ? "—" : FormatDuration(Peaks.Duration);

    /// <summary>播放按钮是否可用。缓存没铺到播放头时必须禁用——按下去也没东西可放。</summary>
    public bool CanPlay => HasAudio && !IsBusy && !IsPreparing;

    /// <summary>
    /// 播放键（按钮与空格）是否可用。
    /// </summary>
    /// <remarks>
    /// 播放中必须保持可用：否则缓存一旦暂时没跟上就把按钮变灰，
    /// 用户会连暂停都按不了，只能眼睁睁看着它播完。
    /// </remarks>
    public bool CanTogglePlayback => HasAudio && !IsBusy && (IsPlaying || !IsPreparing);

    // ------------------------------------------------------------ 载入

    /// <summary>
    /// 解码一个音频文件、显示其波形，并准备播放缓存。
    /// </summary>
    public async Task LoadAsync(string path)
    {
        if (IsBusy) return;

        // 换曲必须清掉歌词，而歌词里可能正躺着用户刚拖出来的改动。
        // 界面层在弹文件对话框之前也会挡一道，这里再挡是为了让「不许覆盖」这件事
        // 由持有文档的这边说了算，而不是靠调用方记得。
        if (HasUnsavedLyrics)
        {
            StatusText = UnsavedLyricsHint;
            return;
        }

        DisposePlayer();

        IsBusy = true;
        Progress = 0;
        FileName = Path.GetFileName(path);
        StatusText = $"正在解码 {FileName} …";

        // .lrc 是音频的侧车文件：换了曲子，上一首的时间轴就不再指向任何东西。
        ClearLyrics();

        try
        {
            // Progress<T> 在构造时捕获 UI 线程的同步上下文，因此 Report 会自动切回 UI 线程。
            var progress = new Progress<double>(value => Progress = value);

            // 4 分钟素材实测约 0.2 秒，但仍然放到线程池上跑，避免以后遇到长文件时界面僵住。
            var result = await Task.Run(() => PeakGenerator.Generate(path, progress));

            Peaks = result.Pyramid;
            _audioPath = path;
            _totalFrames = result.Pyramid.TotalFrames;
            FillDetails(result);

            StatusText = $"已载入 {FileName} · 权威时长 {FormatDuration(result.Pyramid.Duration)}"
                       + $" · 解码耗时 {result.Elapsed.TotalMilliseconds:F0} ms";
        }
        catch (Exception ex)
        {
            Peaks = null;
            Details.Clear();
            StatusText = $"载入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            UpdateReadiness();
        }

        // 播放器单独一步：它要再打开一次解码器，失败也不该把已经出来的波形一起废掉。
        await EnsurePlayerAsync();
    }

    /// <summary>
    /// 建立播放器并开始铺曲首附近的缓存。
    /// </summary>
    /// <remarks>
    /// 刻意放在载入流程之后、并且不阻塞它：播放器构造要打开解码器（几十毫秒），
    /// 而这段等待对「打开文件就能看到波形」毫无贡献。
    /// </remarks>
    private async Task EnsurePlayerAsync()
    {
        var path = _audioPath;
        if (path is null || _player is not null) return;

        var frames = _totalFrames;

        try
        {
            var player = await Task.Run(() => new AudioPlayer(path, frames));

            // 等待期间用户可能又打开了别的文件，那样这个播放器对应的就不是当前音频了。
            if (_disposed || !ReferenceEquals(_audioPath, path))
            {
                player.Dispose();
                return;
            }

            player.CacheChanged += OnPlayerCacheChanged;
            player.StateChanged += OnPlayerStateChanged;
            player.PlaybackEnded += OnPlayerPlaybackEnded;

            _player = player;

            RefreshCacheState();
            RequestPrepare(0);
        }
        catch (Exception ex)
        {
            StatusText = $"无法初始化播放：{ex.Message}";
        }
    }

    private void DisposePlayer()
    {
        _prepareTimer.Stop();
        _tickTimer.Stop();

        var player = _player;
        _player = null;

        if (player is not null)
        {
            player.CacheChanged -= OnPlayerCacheChanged;
            player.StateChanged -= OnPlayerStateChanged;
            player.PlaybackEnded -= OnPlayerPlaybackEnded;
            player.Dispose();
        }

        _audioPath = null;
        _totalFrames = 0;

        IsPlaying = false;
        PlayButtonText = "播放";
        IsPreparing = false;
        _playheadRelocated = false;
        PlayheadSeconds = double.NaN;
        CacheStartSeconds = double.NaN;
        CacheEndSeconds = double.NaN;
        PositionText = "—";
    }

    // ------------------------------------------------------------ 播放控制

    /// <summary>播放 / 暂停 / 继续。</summary>
    /// <remarks>
    /// 暂停与停止在「从哪儿开始放」这一点上是同一回事：都由<b>指示条当前所指的时刻</b>决定。
    /// 唯一的差别是原地点播放可以从暂停处平滑继续，不必重置设备缓冲，
    /// 所以位置没被动过时仍然走 <see cref="AudioPlayer.Resume"/>。
    /// </remarks>
    public void TogglePlayPause()
    {
        var player = _player;
        if (player is null || IsBusy) return;

        if (player.State == AudioPlaybackState.Playing)
        {
            player.Pause();
            SyncPlaybackState();
            return;
        }

        // 位置没被动过才能原地继续。
        if (player.State == AudioPlaybackState.Paused && !_playheadRelocated)
        {
            player.Resume();
            SyncPlaybackState();
            return;
        }

        var target = PlayStartSeconds;

        if (!player.Covers(target))
        {
            // 还没铺到那里。先准备，不自动开始——用户按下的是播放键，
            // 但此刻放不出来，与其放一段静音不如明确告诉他正在准备。
            RequestPrepare(target);
            StatusText = $"正在准备 {FormatDuration(TimeSpan.FromSeconds(target))} 附近的音频…";
            return;
        }

        player.Play(target);
        _playheadRelocated = false;
        SyncPlaybackState();
    }

    partial void OnPlayheadSecondsChanged(double value)
    {
        // 自己刷新播放位置时不算用户操作。
        if (_suppressPlayhead) return;

        var player = _player;
        if (player is null || player.State != AudioPlaybackState.Playing) return;
        if (!double.IsFinite(value)) return;

        if (player.Covers(value))
        {
            // 缓存里就有：直接跳，播放不中断。
            player.Play(value);
            RefreshCacheState();
            return;
        }

        // 缓存之外：刹车是立刻的，铺路要等手停下来。
        //
        // 之所以立刻刹：用户已经听不到他指定的内容了，让他继续听下去毫无意义；
        // 而且按钮必须马上变灰，否则他会以为按钮坏了。播放已经停下，
        // 重新解码的耗时不再触碰任何实时性约束，所以缓存可以放心从头重来。
        //
        // 之所以不立刻解：这一下可能只是拖动途中的某一帧，等一秒往往就跳过去了。
        player.Halt(value);
        SyncPlaybackState();
        UpdateReadiness();
        SchedulePrepare(value);
        StatusText = $"正在准备 {FormatDuration(TimeSpan.FromSeconds(value))} 附近的音频…";
    }

    partial void OnPlayStartSecondsChanged(double value)
    {
        // 播放中播放头由设备时钟驱动，滚动产生的这个值只是中间产物，不该再触发铺缓存。
        if (IsPlaying) return;
        if (!double.IsFinite(value)) return;

        // 暂停或停止时这个值只可能因用户动作而改变：滚动波形、拖动指示条、缩放、复位。
        // 播放中那条路径已被上面的判断挡掉，而暂停时没有任何东西会自发去写它。
        // 于是这里可以放心断定「用户把播放头挪到别处了」。
        _playheadRelocated = true;

        SchedulePrepare(value);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (value) _tickTimer.Start();
        else _tickTimer.Stop();
    }

    private void OnTickTimerTick(object? sender, EventArgs e)
    {
        var player = _player;
        if (player is null || player.State != AudioPlaybackState.Playing)
        {
            _tickTimer.Stop();
            return;
        }

        var position = player.PositionSeconds;
        SetPlayhead(position);

        // 只精确到 0.1 秒：这个文本每变一次都要走一轮绑定与布局，
        // 毫秒级的话每帧都在变，等于给渲染又加了一份固定开销。
        PositionText = $"{FormatPosition(TimeSpan.FromSeconds(position))} / {DurationText}";
    }

    private void SetPlayhead(double seconds)
    {
        _suppressPlayhead = true;
        try
        {
            PlayheadSeconds = seconds;
        }
        finally
        {
            _suppressPlayhead = false;
        }
    }

    // ------------------------------------------------------------ 缓存准备

    private void OnPrepareTimerTick(object? sender, EventArgs e)
    {
        _prepareTimer.Stop();
        RequestPrepare(_pendingPrepareSeconds);
    }

    private void SchedulePrepare(double seconds)
    {
        _pendingPrepareSeconds = seconds;
        _prepareTimer.Stop();
        _prepareTimer.Start();
    }

    private void RequestPrepare(double seconds)
    {
        // 立即准备意味着先前排队的延迟准备已经多余，留着只会在一秒后白解一次。
        _prepareTimer.Stop();

        var player = _player;
        if (player is null) return;

        player.Prepare(seconds);
        RefreshCacheState();
    }

    private void OnPlayerCacheChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshCacheState);

    private void OnPlayerStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncPlaybackState);

    private void OnPlayerPlaybackEnded(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(HandlePlaybackEnded);

    private void RefreshCacheState()
    {
        var player = _player;

        if (player is null)
        {
            CacheStartSeconds = double.NaN;
            CacheEndSeconds = double.NaN;
            IsPreparing = false;
            return;
        }

        CacheStartSeconds = player.CacheStartSeconds;
        CacheEndSeconds = player.CacheEndSeconds;

        // 解码失败时不能把按钮永久锁死——此后不会再有事件来解除它。
        if (player.Failure is { } failure)
        {
            IsPreparing = false;
            StatusText = $"解码失败：{failure.Message}";
            return;
        }

        UpdateReadiness();
    }

    private void UpdateReadiness()
    {
        var player = _player;
        IsPreparing = player is not null && !player.Covers(PlayStartSeconds);
    }

    private void SyncPlaybackState()
    {
        var playing = _player?.State == AudioPlaybackState.Playing;

        IsPlaying = playing;
        PlayButtonText = playing ? "暂停" : "播放";

        UpdateReadiness();
    }

    private void HandlePlaybackEnded()
    {
        IsPlaying = false;
        PlayButtonText = "播放";
        StatusText = "播放结束";

        if (_player is { } player) SetPlayhead(player.DurationSeconds);
    }

    // ------------------------------------------------------------ 其它

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePlayer();
    }

    private void FillDetails(PeakGenerationResult result)
    {
        var pyramid = result.Pyramid;
        var info = result.Info;

        var speed = result.Elapsed.TotalSeconds > 1e-6
            ? pyramid.DurationSeconds / result.Elapsed.TotalSeconds
            : 0;

        Details.Clear();
        Details.Add(new InfoRow("文件名", Path.GetFileName(info.Path)));
        Details.Add(new InfoRow("编解码器", info.Summary));
        Details.Add(new InfoRow("权威时长", FormatDuration(pyramid.Duration)));
        Details.Add(new InfoRow("总帧数", $"{pyramid.TotalFrames:N0} 帧/声道"));
        Details.Add(new InfoRow(
            "峰值金字塔",
            $"{pyramid.Levels.Count} 级 · 最细 {PeakGenerator.BaseBucketFrames} 帧/桶 · "
            + $"内存 {pyramid.ApproximateBytes / 1024.0:F0} KB"));
        Details.Add(new InfoRow("解码性能", $"{result.Elapsed.TotalMilliseconds:F0} ms（约 {speed:F0} 倍实时）"));
        Details.Add(new InfoRow(
            "播放缓存",
            $"{PcmBlockStore.BlockFrames} 帧/块 · 立体声每块 {PcmBlockStore.BlockFrames * 8 / 1024} KB · "
            + $"上限随可用内存浮动，当前 {SystemMemory.CacheBudget() / 1024.0 / 1024.0:F0} MB"));
        Details.Add(new InfoRow(
            "FFmpeg 运行库",
            $"{FfmpegRuntime.RuntimeVersion ?? "未加载"}\n{FfmpegRuntime.LibraryPath ?? "—"}"));

        if (info.ContainerDuration > TimeSpan.Zero)
        {
            var drift = (info.ContainerDuration - pyramid.Duration).TotalMilliseconds;
            Details.Add(new InfoRow(
                "容器声明时长",
                $"{FormatDuration(info.ContainerDuration)}（与权威时长相差 {drift:F0} ms）"));
        }
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.fff") : value.ToString(@"mm\:ss\.fff");

    /// <summary>
    /// 播放位置的显示格式，只到 0.1 秒。
    /// </summary>
    /// <remarks>
    /// 刻意比 <see cref="FormatDuration"/> 粗糙一档：这个文本在播放中每秒要重写若干次，
    /// 每写一次都带走一轮绑定与布局，而毫秒位的跳动对「现在放到哪儿了」毫无信息量。
    /// </remarks>
    private static string FormatPosition(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.f") : value.ToString(@"mm\:ss\.f");
}
