using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SimpleLyrics.Core.Audio;

/// <summary>
/// 基于 FFmpeg 共享库的解码器：把任意受支持的音频文件解成
/// <b>交错 32 位浮点 PCM</b>，并顺带给出权威时长所需的样本计数。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖 MP3 / FLAC / AAC（ADTS 与 M4A）/ OGG Vorbis 等——凡是该发行包带解码器的格式都能解，
/// 与 Windows 自带解码链（媒体基础）无关，因此不受系统编解码器与沙箱限制。
/// </para>
/// <para>
/// <b>内存所有权</b>：所有 FFmpeg 上下文（<c>AVFormatContext</c> / <c>AVCodecContext</c> /
/// <c>AVPacket</c> / <c>AVFrame</c>）一律由 DLL 侧 <c>*_alloc</c> 分配，托管侧只通过指针读写字段，
/// 绝不在栈上构造这些结构体。这是绑定版本与运行库次版本号不一致时仍然安全的前提。
/// </para>
/// <para>
/// <b>线程模型</b>：一个实例只能被一个线程使用，不支持并发调用。
/// </para>
/// </remarks>
public sealed unsafe class FfmpegDecoder : IDisposable
{
    /// <summary>FFmpeg 内部时间基：1 秒 = 1,000,000。</summary>
    private const long AvTimeBase = 1_000_000;

    private AVFormatContext* _format;
    private AVCodecContext* _decoder;
    private SwrContext* _swr;
    private AVPacket* _packet;
    private AVFrame* _frame;

    private float* _output;
    private int _outputCapacity;

    private int _audioStream = -1;

    /// <summary>打开时数出来的音频流总字节数（只看包头）；0 表示没数成。</summary>
    private long _audioBytes;

    // 当前重采样器对应的输入参数，用于识别解码中途格式变化。
    private int _inputSampleRate;
    private int _inputChannels;
    private AVSampleFormat _inputFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;

    private bool _disposed;

    /// <summary>是否已读到末尾并冲刷完毕。置位后 <see cref="DecodeChunk"/> 直接返回 0。</summary>
    private bool _finished;

    private FfmpegDecoder()
    {
    }

    /// <summary>文件元信息。</summary>
    public AudioFileInfo Info { get; private set; } = null!;

    /// <summary>实际输出格式。采样率跟随源文件，声道数由打开时指定。</summary>
    public PcmFormat OutputFormat { get; private set; }

    /// <summary>
    /// 打开音频文件并准备解码。
    /// </summary>
    /// <param name="path">音频文件路径。</param>
    /// <param name="outputChannels">
    /// 输出声道数。波形生成用 2（可同时保留左右声道）；纯单声道场景可用 1。
    /// 源为单声道而输出为 2 时，重采样器会把内容复制到两个声道。
    /// </param>
    public static FfmpegDecoder Open(string path, int outputChannels = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (outputChannels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(outputChannels), outputChannels, "输出声道数只支持 1 或 2");
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到音频文件。", path);

        FfmpegRuntime.EnsureInitialized(Path.GetDirectoryName(path));

        var decoder = new FfmpegDecoder();
        try
        {
            decoder.OpenCore(path, outputChannels);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary><see cref="DecodeTo"/> 每次内部分块的大小，只影响取消请求的响应粒度。</summary>
    private const int DefaultChunkFrames = 1 << 16;

    /// <summary>是否已经解码到文件末尾。</summary>
    public bool IsFinished => _finished;

    /// <summary>
    /// 完整解码一遍，把每个 PCM 块交给 <paramref name="sink"/>。
    /// </summary>
    /// <returns>累计输出的帧数（每声道）。除以采样率即为权威时长。</returns>
    /// <remarks>
    /// 本方法会读到底（含冲刷解码器与重采样器的内部残留），
    /// 因此一次调用就能同时得到精确时长与完整波形数据。
    /// </remarks>
    public long DecodeTo(IPcmSink sink, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        var estimatedFrames = Info.ContainerDuration.TotalSeconds > 0
            ? Info.ContainerDuration.TotalSeconds * OutputFormat.SampleRate
            : 0;
        var lastReported = 0d;

        long frames = 0;

        while (true)
        {
            // 分块调用而不是一口气读到底：这样取消请求最多延迟一块的时长就能生效。
            var chunk = DecodeChunk(sink, DefaultChunkFrames, cancellationToken);
            if (chunk == 0) break;

            frames += chunk;

            if (progress is not null && estimatedFrames > 0)
            {
                var value = Math.Min(1d, frames / estimatedFrames);
                if (value - lastReported >= 0.01)
                {
                    lastReported = value;
                    progress.Report(value);
                }
            }
        }

        progress?.Report(1d);
        return frames;
    }

    /// <summary>
    /// 增量解码：最多产出约 <paramref name="maxFrames"/> 帧就返回，可反复调用直到返回 0。
    /// </summary>
    /// <returns>本次产出的帧数（每声道）。返回 0 表示已经到达文件末尾。</returns>
    /// <remarks>
    /// <para>
    /// 这是为播放缓存服务的：缓存需要「解一段就停下来问问还要不要继续」的能力，
    /// 而不是一次读到底。块边界与解码帧边界无关，所以实际产出可能略多于
    /// <paramref name="maxFrames"/>——多出来的是最后一整个音频帧。
    /// </para>
    /// <para>
    /// <b>提前返回不会破坏解码器状态</b>：<c>av_read_frame</c> 的读取位置、
    /// 解码器内部尚未取出的帧都原样保留，下一次调用从这里接着走。
    /// 只有真正读到末尾时才会冲刷解码器与重采样器并置位结束标志。
    /// </para>
    /// </remarks>
    public long DecodeChunk(IPcmSink sink, long maxFrames, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        if (_finished || maxFrames <= 0) return 0;

        long frames = 0;

        while (frames < maxFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ffmpeg.av_read_frame(_format, _packet) < 0)
            {
                frames += Finish(sink);
                _finished = true;
                break;
            }

            if (_packet->stream_index == _audioStream)
            {
                var sent = ffmpeg.avcodec_send_packet(_decoder, _packet);

                // 解码器缓冲区满时先取帧再重发；这是 send/receive 组合接口的标准用法。
                if (sent == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    frames += DrainFrames(sink);
                    sent = ffmpeg.avcodec_send_packet(_decoder, _packet);
                }

                if (sent < 0 && sent != ffmpeg.AVERROR_EOF)
                    throw new InvalidOperationException($"avcodec_send_packet 失败：{Error(sent)}");

                frames += DrainFrames(sink);
            }

            ffmpeg.av_packet_unref(_packet);
        }

        return frames;
    }

    /// <summary>冲刷解码器与重采样器里的残留帧。</summary>
    /// <remarks>
    /// 少了这一步，末尾若干帧会计不到——时长偏短，波形尾部会被截掉。
    /// </remarks>
    private long Finish(IPcmSink sink)
    {
        var flush = ffmpeg.avcodec_send_packet(_decoder, null);
        if (flush < 0 && flush != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException($"avcodec_send_packet(冲刷) 失败：{Error(flush)}");

        // 冲刷重采样器残留（重采样会引入若干样本的延迟）。
        return DrainFrames(sink) + FlushResampler(sink);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_output is not null)
        {
            NativeMemory.Free(_output);
            _output = null;
        }

        // 释放顺序与创建顺序相反。这些指针都是 DLL 分配的，交回 DLL 释放。
        //
        // 必须先抄到局部变量：托管对象的字段不能取地址（CS0212），
        // 而且 *_free 系列会把传入的指针置空，局部变量正好可直接丢弃。
        var swr = _swr;
        var frame = _frame;
        var packet = _packet;
        var decoder = _decoder;
        var format = _format;

        if (swr is not null) ffmpeg.swr_free(&swr);
        if (frame is not null) ffmpeg.av_frame_free(&frame);
        if (packet is not null) ffmpeg.av_packet_free(&packet);
        if (decoder is not null) ffmpeg.avcodec_free_context(&decoder);
        if (format is not null) ffmpeg.avformat_close_input(&format);

        _swr = null;
        _frame = null;
        _packet = null;
        _decoder = null;
        _format = null;
    }

    // ---------------------------------------------------------------- 打开

    private void OpenCore(string path, int outputChannels)
    {
        AVFormatContext* format = null;
        Check(ffmpeg.avformat_open_input(&format, path, null, null), "avformat_open_input");
        _format = format;

        Check(ffmpeg.avformat_find_stream_info(_format, null), "avformat_find_stream_info");

        AVCodec* codec = null;
        _audioStream = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
        if (_audioStream < 0)
            throw new InvalidOperationException($"文件中没有可解码的音频流（av_find_best_stream：{Error(_audioStream)}）");

        var stream = _format->streams[_audioStream];

        _decoder = ffmpeg.avcodec_alloc_context3(codec);
        if (_decoder is null) throw new InvalidOperationException("avcodec_alloc_context3 返回空指针");

        Check(ffmpeg.avcodec_parameters_to_context(_decoder, stream->codecpar), "avcodec_parameters_to_context");
        Check(ffmpeg.avcodec_open2(_decoder, codec, null), "avcodec_open2");

        // 个别容器在 open2 之后仍可能没把采样率写进解码器上下文，退回流参数。
        var sampleRate = _decoder->sample_rate > 0 ? _decoder->sample_rate : stream->codecpar->sample_rate;
        if (sampleRate <= 0)
            throw new InvalidOperationException("解码器未报告有效采样率，无法建立时间轴");

        OutputFormat = new PcmFormat(sampleRate, outputChannels);

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet is null || _frame is null)
            throw new InvalidOperationException("分配 AVPacket / AVFrame 失败");

        var containerDuration = _format->duration > 0
            ? TimeSpan.FromSeconds((double)_format->duration / AvTimeBase)
            : TimeSpan.Zero;

        // 音频流自己的时长优先：容器时长对 ADTS 之类的裸流只能按码率估算，而流的时长
        // 由该流的最后一个时间戳给出，离真实的音频长度更近。
        var timeBase = ffmpeg.av_q2d(stream->time_base);
        var streamDuration = stream->duration > 0 && timeBase > 0
            ? TimeSpan.FromSeconds(stream->duration * timeBase)
            : containerDuration;

        Info = new AudioFileInfo(
            path,
            ffmpeg.avcodec_get_name(_decoder->codec_id) ?? "?",
            PtrToString(_format->iformat is null ? null : _format->iformat->name),
            sampleRate,
            _decoder->ch_layout.nb_channels,
            containerDuration,
            MeasureAverageBitRate(streamDuration, stream->codecpar->bit_rate));

        // 多数情况下解码器此时已经报告了采样格式；少数格式要等第一帧，届时会重建。
        if (_decoder->sample_fmt != AVSampleFormat.AV_SAMPLE_FMT_NONE && _decoder->ch_layout.nb_channels > 0)
            SetupResampler(_decoder->sample_rate, &_decoder->ch_layout, _decoder->sample_fmt);
    }

    /// <summary>
    /// 这一段音频的真实平均码率（bit/s）：把<b>这一个音频流</b>的全部包字节数加起来，除以时长。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不用 <c>codecpar-&gt;bit_rate</c>。</b>那个字段有两处会偏：ADTS 之类的裸流上它是
    /// FFmpeg 在探测阶段按几帧估出来的；MP4 / M4A 里的 AAC 则是容器 <c>esds</c> 里写的
    /// <b>标称</b>码率，与真实平均值不是一回事。实测 AAC 上报 228 kbps 而真实值在 250 以上，
    /// 差了两成——而用户会拿这个数字与转码软件一对，对不上就以为本工具读错了文件。
    /// 它唯一的作用就是告诉用户「这是什么规格的音频」，所以宁可自己算。
    /// </para>
    /// <para>
    /// <b>也不拿文件大小去除。</b>文件里可能还有封面图、其他流与容器开销，除出来的不是音频码率。
    /// 这里只累加<b>这一个流</b>的包，封面无论多大都不会算进来。
    /// </para>
    /// <para>
    /// 代价是把容器顺序读一遍——<b>只看包头，不解码</b>。四分钟的歌在毫秒量级，而打开文件之后
    /// 紧接着就要整曲解码做波形，相比之下可以忽略。读完 seek 回开头，调用方看不出这件事。
    /// </para>
    /// <para>
    /// 算不出来时退回容器上报的那个值：宁可给一个可能偏低的数字，也不要让这一栏空着——
    /// 空着看不出是「没有这个信息」还是「读失败了」。
    /// </para>
    /// </remarks>
    /// <param name="duration">音频流的时长；为零时无法平均，直接退回上报值。</param>
    /// <param name="reported">容器上报的码率，作为退路。</param>
    private long MeasureAverageBitRate(TimeSpan duration, long reported)
    {
        if (_format is null || duration <= TimeSpan.Zero) return Math.Max(reported, 0);

        var packet = ffmpeg.av_packet_alloc();
        if (packet is null) return Math.Max(reported, 0);

        var bytes = 0L;

        try
        {
            // 探测阶段可能把读位子留在中间，先明确回到开头再数。
            ffmpeg.av_seek_frame(_format, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);

            while (ffmpeg.av_read_frame(_format, packet) >= 0)
            {
                if (packet->stream_index == _audioStream) bytes += packet->size;

                ffmpeg.av_packet_unref(packet);
            }
        }
        catch (Exception)
        {
            // 读不完整就用不上这个平均值——退回上报值，而不是让整个文件打不开。
            return Math.Max(reported, 0);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);

            // 无论数到哪儿都要回到开头：调用方以为解码器还停在起始位置。
            ffmpeg.av_seek_frame(_format, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }

        if (bytes <= 0) return Math.Max(reported, 0);

        _audioBytes = bytes;

        return (long)Math.Round(bytes * 8 / duration.TotalSeconds);
    }

    /// <summary>
    /// 用<b>精确时长</b>重算平均码率；拿不到精确时长（或没数成字节数）时退回打开时那个值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 打开文件时只能按容器（或流）声明的时长去平均，而那个时长对 ADTS 之类的裸流是估算的
    /// ——实测偏差近 30 秒，码率因此会跟着偏小一两成。整曲解码完就有了权威时长
    /// （<see cref="Peaks.PeakPyramid.Duration"/>，由累计样本数得出），用它再算一遍才是真值。
    /// </para>
    /// <para>
    /// 字节数只数一遍就够（打开时数过），所以这里不读文件，只是换个分母做除法。
    /// </para>
    /// </remarks>
    public long ResolveBitRate(TimeSpan exactDuration)
    {
        if (_audioBytes <= 0 || exactDuration <= TimeSpan.Zero) return Info.BitRate;

        return (long)Math.Round(_audioBytes * 8 / exactDuration.TotalSeconds);
    }

    // ------------------------------------------------------------ 重采样器

    /// <summary>
    /// 按给定的输入参数（重新）建立重采样器，统一输出为交错 float32。
    /// </summary>
    private void SetupResampler(int inputSampleRate, AVChannelLayout* inputLayout, AVSampleFormat inputFormat)
    {
        if (inputSampleRate <= 0)
            throw new InvalidOperationException($"重采样器输入采样率无效：{inputSampleRate}");

        var previous = _swr;

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, OutputFormat.Channels);

        SwrContext* created = null;
        try
        {
            var ret = ffmpeg.swr_alloc_set_opts2(
                &created,
                &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutputFormat.SampleRate,
                inputLayout, inputFormat, inputSampleRate,
                0, null);
            Check(ret, "swr_alloc_set_opts2");

            Check(ffmpeg.swr_init(created), "swr_init");
        }
        catch
        {
            if (created is not null) ffmpeg.swr_free(&created);
            throw;
        }
        finally
        {
            // 默认声道布局若声道数较多会在堆上分配声道表，必须归还。
            ffmpeg.av_channel_layout_uninit(&outputLayout);
        }

        _swr = created;
        if (previous is not null) ffmpeg.swr_free(&previous);

        _inputSampleRate = inputSampleRate;
        _inputChannels = inputLayout->nb_channels;
        _inputFormat = inputFormat;
    }

    /// <summary>解码中途输入格式发生变化（罕见，但有些容器会这样）时重建。</summary>
    private void EnsureResamplerFor(AVFrame* frame)
    {
        var format = (AVSampleFormat)frame->format;
        if (_swr is not null
            && _inputSampleRate == frame->sample_rate
            && _inputChannels == frame->ch_layout.nb_channels
            && _inputFormat == format)
        {
            return;
        }

        SetupResampler(frame->sample_rate, &frame->ch_layout, format);
    }

    // ---------------------------------------------------------------- 解码

    /// <summary>取出解码器当前所有可用帧。</summary>
    private long DrainFrames(IPcmSink sink)
    {
        long frames = 0;
        int ret;

        while ((ret = ffmpeg.avcodec_receive_frame(_decoder, _frame)) >= 0)
        {
            EnsureResamplerFor(_frame);
            frames += ConvertAndWrite(sink);
            ffmpeg.av_frame_unref(_frame);
        }

        // EAGAIN = 需要更多输入；EOF = 已冲刷干净。两者都是正常收尾。
        if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException($"avcodec_receive_frame 失败：{Error(ret)}");

        return frames;
    }

    /// <summary>把一帧转换成输出格式并推给 sink。</summary>
    private long ConvertAndWrite(IPcmSink sink)
    {
        var channels = OutputFormat.Channels;

        // 重采样可能改变样本数（本方案采样率相同，通常 1:1），留出余量即可，
        // 不必动用 av_rescale_rnd —— 少一个版本相关的 API 面。
        var needed = (int)((_frame->nb_samples + 64L) * OutputFormat.SampleRate / _inputSampleRate) + 64;
        EnsureOutputBuffer(needed);

        var outPtr = (byte*)_output;
        var produced = ffmpeg.swr_convert(_swr, &outPtr, needed, _frame->extended_data, _frame->nb_samples);
        if (produced < 0)
            throw new InvalidOperationException($"swr_convert 失败：{Error(produced)}");
        if (produced == 0) return 0;

        sink.Write(new ReadOnlySpan<float>(_output, produced * channels), produced);
        return produced;
    }

    /// <summary>把重采样器内部残留的样本挤出来。</summary>
    private long FlushResampler(IPcmSink sink)
    {
        if (_swr is null) return 0;

        const int capacity = 4096;
        var channels = OutputFormat.Channels;
        EnsureOutputBuffer(capacity);

        long total = 0;
        for (var guard = 0; guard < 16; guard++)
        {
            var outPtr = (byte*)_output;
            var produced = ffmpeg.swr_convert(_swr, &outPtr, capacity, null, 0);
            if (produced < 0)
                throw new InvalidOperationException($"swr_convert(冲刷) 失败：{Error(produced)}");
            if (produced == 0) break;

            sink.Write(new ReadOnlySpan<float>(_output, produced * channels), produced);
            total += produced;
        }

        return total;
    }

    private void EnsureOutputBuffer(int frames)
    {
        if (_outputCapacity >= frames && _output is not null) return;

        if (_output is not null) NativeMemory.Free(_output);

        _outputCapacity = frames;
        _output = (float*)NativeMemory.Alloc((nuint)frames * (nuint)OutputFormat.Channels, sizeof(float));
    }

    // ---------------------------------------------------------------- 辅助

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what} 失败：{Error(result)}");
    }

    private static string Error(int code)
    {
        var buffer = stackalloc byte[512];
        ffmpeg.av_strerror(code, buffer, 512);
        var text = Marshal.PtrToStringAnsi((nint)buffer);
        return string.IsNullOrWhiteSpace(text) ? $"错误码 {code}" : $"{text}（{code}）";
    }

    private static string PtrToString(byte* value) =>
        value is null ? "?" : Marshal.PtrToStringAnsi((nint)value) ?? "?";
}
