using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SimpleLyrics.Probe;

/// <summary>
/// FFmpeg 可行性探针：验证 FFmpeg.AutoGen 托管绑定能否加载本机共享库，
/// 并测量「完整解码累计样本数」得到的权威时长。
/// </summary>
/// <remarks>
/// 只需 4 个 DLL 即可自足（见 PLAN.md §3.3）：
///   avformat-63 → avcodec-63 → { avutil-61, swresample-7 }
/// 不需要 avfilter / avdevice / swscale。
/// </remarks>
internal static unsafe class FfmpegProbe
{
    /// <summary>FFmpeg 内部时间基：1 秒 = 1,000,000。</summary>
    private const int TimeBase = 1_000_000;

    /// <summary>格式注册名 → 展示名。</summary>
    private static readonly (string File, string Label)[] Targets =
    [
        ("test.mp3", "MP3"),
        ("test.flac", "FLAC"),
        ("test.aac", "AAC(ADTS)"),
        ("test.ogg", "OGG/Vorbis"),
    ];

    public static void Run(string audioRoot)
    {
        Console.WriteLine("---- FFmpeg 探针 ----");

        var bin = LocateLibraries(audioRoot);
        if (bin is null)
        {
            Console.WriteLine("  × 未找到含 avcodec-*.dll 的 FFmpeg 目录");
            Console.WriteLine("    （可用环境变量 SL_FFMPEG_BIN 显式指定）");
            Console.WriteLine();
            return;
        }

        ffmpeg.RootPath = bin;
        Console.WriteLine($"  库目录     : {bin}");

        // 压掉 FFmpeg 自身的告警噪音（ADTS 时长估算、时间戳跳过等）。
        // 产品中应改为 av_log_set_callback 转发到自己的日志系统，而不是直接丢弃。
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        string runtime;
        try
        {
            runtime = ffmpeg.av_version_info();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  × 加载失败 : {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("    （DLL 缺失或位数不匹配时会出现此情况）");
            Console.WriteLine();
            return;
        }

        // 绑定常量与运行时版本必须同为 avcodec 63 / avutil 61，否则 ABI 不保证兼容。
        Console.WriteLine($"  运行时版本 : {runtime}");
        Console.WriteLine($"  绑定版本   : avcodec {ffmpeg.LIBAVCODEC_VERSION_MAJOR}.{ffmpeg.LIBAVCODEC_VERSION_MINOR}.{ffmpeg.LIBAVCODEC_VERSION_MICRO}"
                        + $" / avutil {ffmpeg.LIBAVUTIL_VERSION_MAJOR}.{ffmpeg.LIBAVUTIL_VERSION_MINOR}.{ffmpeg.LIBAVUTIL_VERSION_MICRO}");
        Console.WriteLine($"  实际版本号 : avcodec {Unpack(ffmpeg.avcodec_version())} / avutil {Unpack(ffmpeg.avutil_version())}"
                        + $" / avformat {Unpack(ffmpeg.avformat_version())}");

        var abiMatch = (ffmpeg.avcodec_version() >> 16) == ffmpeg.LIBAVCODEC_VERSION_MAJOR
                    && (ffmpeg.avutil_version() >> 16) == ffmpeg.LIBAVUTIL_VERSION_MAJOR;
        Console.WriteLine($"  ABI 匹配   : {(abiMatch ? "是" : "否 —— 绑定与 DLL 主版本不一致，有崩溃风险")}");
        Console.WriteLine();

        Console.WriteLine("---- 解码与权威时长（完整解码累计样本数） ----");
        Console.WriteLine("  格式         编解码器      采样率  声道  容器时长      精确时长       误差        样本数        耗时    速度");

        foreach (var (file, label) in Targets)
        {
            var path = TestAudio.Resolve(audioRoot, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"  {label,-12} 不存在");
                continue;
            }

            try
            {
                var r = Decode(path);
                var deltaMs = (r.ContainerDuration - r.ExactDuration).TotalMilliseconds;

                Console.WriteLine(
                    $"  {label,-12} {r.CodecName,-13} {r.SampleRate,6}  {r.Channels,3}  " +
                    $"{Fmt(r.ContainerDuration)}  {Fmt(r.ExactDuration)}  {deltaMs,8:F0} ms  " +
                    $"{r.Samples,12:N0}  {r.Elapsed.TotalSeconds,6:F2}s  {r.SpeedRatio,5:F0}x");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-12} 失败: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  说明：容器时长对 ADTS AAC 只能估算（无时长字段）；精确时长才是时间轴基准。");
        Console.WriteLine();

        TestSeekPrecision(audioRoot);
    }

    /// <summary>完整解码一个文件，累计样本数，得出权威时长。</summary>
    private static DecodeResult Decode(string path)
    {
        AVFormatContext* format = null;
        AVCodecContext* decoder = null;
        AVPacket* packet = null;
        AVFrame* frame = null;

        try
        {
            Check(ffmpeg.avformat_open_input(&format, path, null, null), "avformat_open_input");
            Check(ffmpeg.avformat_find_stream_info(format, null), "avformat_find_stream_info");

            AVCodec* codec = null;
            var audioStream = ffmpeg.av_find_best_stream(
                format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            Check(audioStream, "av_find_best_stream");

            var stream = format->streams[audioStream];

            decoder = ffmpeg.avcodec_alloc_context3(codec);
            if (decoder is null) throw new InvalidOperationException("avcodec_alloc_context3 返回空");

            Check(ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar), "avcodec_parameters_to_context");
            Check(ffmpeg.avcodec_open2(decoder, codec, null), "avcodec_open2");

            var sampleRate = decoder->sample_rate;
            if (sampleRate <= 0) throw new InvalidOperationException($"无效采样率 {sampleRate}");

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (packet is null || frame is null) throw new InvalidOperationException("分配 packet/frame 失败");

            var stopwatch = Stopwatch.StartNew();
            long samples = 0;
            var packets = 0;

            int ret;
            while ((ret = ffmpeg.av_read_frame(format, packet)) >= 0)
            {
                if (packet->stream_index == audioStream)
                {
                    packets++;
                    Check(ffmpeg.avcodec_send_packet(decoder, packet), "avcodec_send_packet");
                    samples += Drain(decoder, frame);
                }

                ffmpeg.av_packet_unref(packet);
            }

            // 冲刷解码器内部缓冲，否则末尾若干帧会漏计。
            Check(ffmpeg.avcodec_send_packet(decoder, null), "avcodec_send_packet(flush)");
            samples += Drain(decoder, frame);

            stopwatch.Stop();

            var exact = TimeSpan.FromSeconds((double)samples / sampleRate);
            var containerTicks = format->duration > 0
                ? TimeSpan.FromSeconds((double)format->duration / TimeBase)
                : TimeSpan.Zero;

            var codecName = ffmpeg.avcodec_get_name(decoder->codec_id);

            return new DecodeResult(
                codecName,
                sampleRate,
                decoder->ch_layout.nb_channels,
                containerTicks,
                exact,
                samples,
                packets,
                stopwatch.Elapsed,
                exact.TotalSeconds > 0 ? exact.TotalSeconds / stopwatch.Elapsed.TotalSeconds : 0);
        }
        finally
        {
            if (frame is not null) ffmpeg.av_frame_free(&frame);
            if (packet is not null) ffmpeg.av_packet_free(&packet);
            if (decoder is not null) ffmpeg.avcodec_free_context(&decoder);
            if (format is not null) ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>测量 av_seek_frame 的实际落点偏差——这是打轴软件的精度地基。</summary>
    private static void TestSeekPrecision(string audioRoot)
    {
        Console.WriteLine("---- FFmpeg seek 精度 ----");
        Console.WriteLine("  记录 seek 后第一帧的实际时间戳；偏差为负表示落在目标之前（backward 语义，可再解码丢弃补正）。");
        Console.WriteLine();

        double[] targets = [29.372, 58.744, 88.115, 117.487, 146.859, 176.230, 205.602];

        foreach (var (file, label) in Targets)
        {
            var path = TestAudio.Resolve(audioRoot, file);
            if (!File.Exists(path)) continue;

            AVFormatContext* format = null;
            AVCodecContext* decoder = null;
            AVPacket* packet = null;
            AVFrame* frame = null;

            try
            {
                Check(ffmpeg.avformat_open_input(&format, path, null, null), "avformat_open_input");
                Check(ffmpeg.avformat_find_stream_info(format, null), "avformat_find_stream_info");

                AVCodec* codec = null;
                var audioStream = ffmpeg.av_find_best_stream(
                    format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
                Check(audioStream, "av_find_best_stream");

                var stream = format->streams[audioStream];
                decoder = ffmpeg.avcodec_alloc_context3(codec);
                Check(ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar), "avcodec_parameters_to_context");
                Check(ffmpeg.avcodec_open2(decoder, codec, null), "avcodec_open2");

                packet = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();

                var timeBase = ffmpeg.av_q2d(stream->time_base);
                var frameSamples = decoder->frame_size;
                var frameMs = frameSamples > 0 ? frameSamples * timeBase * 1000 : 0;

                Console.WriteLine(
                    $"  [{label}] time_base={stream->time_base.num}/{stream->time_base.den}"
                    + $"  帧长={frameSamples} 样本 ≈ {frameMs:F1} ms");

                foreach (var target in targets)
                {
                    // av_seek_frame 的 timestamp 单位取决于 stream_index：指定具体流时用该流的 time_base。
                    var timestamp = (long)Math.Round(target / timeBase);
                    var ret = ffmpeg.av_seek_frame(format, audioStream, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (ret < 0)
                    {
                        Console.WriteLine($"    请求 {target,8:F3}s → seek 失败: {Error(ret)}");
                        continue;
                    }

                    // seek 后必须清空解码器内部缓冲，否则会取到 seek 之前的帧。
                    ffmpeg.avcodec_flush_buffers(decoder);

                    var landed = double.NaN;
                    while (ffmpeg.av_read_frame(format, packet) >= 0)
                    {
                        if (packet->stream_index == audioStream)
                        {
                            var sent = ffmpeg.avcodec_send_packet(decoder, packet);
                            if (sent >= 0 && ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
                            {
                                landed = frame->best_effort_timestamp * timeBase;
                                ffmpeg.av_frame_unref(frame);
                                ffmpeg.av_packet_unref(packet);
                                break;
                            }
                        }

                        ffmpeg.av_packet_unref(packet);
                    }

                    Console.WriteLine(double.IsNaN(landed)
                        ? $"    请求 {target,8:F3}s → 未取到帧"
                        : $"    请求 {target,8:F3}s → 实际 {landed,8:F3}s   偏差 {(landed - target) * 1000,7:F1} ms");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{label}] 失败: {ex.Message}");
            }
            finally
            {
                if (frame is not null) ffmpeg.av_frame_free(&frame);
                if (packet is not null) ffmpeg.av_packet_free(&packet);
                if (decoder is not null) ffmpeg.avcodec_free_context(&decoder);
                if (format is not null) ffmpeg.avformat_close_input(&format);
            }

            Console.WriteLine();
        }
    }

    /// <summary>取出解码器当前所有可用帧，累计样本数。</summary>
    private static long Drain(AVCodecContext* decoder, AVFrame* frame)
    {
        long samples = 0;
        int ret;

        while ((ret = ffmpeg.avcodec_receive_frame(decoder, frame)) >= 0)
        {
            samples += frame->nb_samples;
            ffmpeg.av_frame_unref(frame);
        }

        // AVERROR(EAGAIN) 与 AVERROR_EOF 都属于正常结束；其余是真实错误。
        if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException($"avcodec_receive_frame: {Error(ret)}");

        return samples;
    }

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what}: {Error(result)}");
    }

    private static string Error(int code)
    {
        var buffer = stackalloc byte[512];
        ffmpeg.av_strerror(code, buffer, 512);
        var text = Marshal.PtrToStringAnsi((nint)buffer);
        return string.IsNullOrWhiteSpace(text) ? $"错误码 {code}" : $"{text} ({code})";
    }

    private static string Unpack(uint version) =>
        $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";

    private static string Fmt(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff");

    /// <summary>
    /// 定位含 avcodec-*.dll 的目录。
    /// 查找顺序：环境变量 <c>SL_FFMPEG_BIN</c> → 仓库根的 third_party/ffmpeg/bin
    /// → 从输出目录向上找到的仓库根 → 输出目录旁的 ffmpeg（发布形态）。
    /// </summary>
    private static string? LocateLibraries(string audioRoot)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("SL_FFMPEG_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && HasAvCodec(fromEnvironment)) return fromEnvironment;

        // 开发形态：音频目录通常就是仓库根。
        var inRepository = Path.Combine(audioRoot, "third_party", "ffmpeg", "bin");
        if (HasAvCodec(inRepository)) return inRepository;

        // 在 VS 中运行时，BaseDirectory 位于 src/<项目>/bin/<配置>/<TFM>/，需逐级回溯到仓库根。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "ffmpeg", "bin");
            if (HasAvCodec(candidate)) return candidate;
        }

        // 发布形态：DLL 被拷到 exe 旁边的 ffmpeg 目录。
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        return HasAvCodec(beside) ? beside : null;
    }

    private static bool HasAvCodec(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "avcodec-*.dll").Any();

    private sealed record DecodeResult(
        string CodecName,
        int SampleRate,
        int Channels,
        TimeSpan ContainerDuration,
        TimeSpan ExactDuration,
        long Samples,
        int Packets,
        TimeSpan Elapsed,
        double SpeedRatio);
}
