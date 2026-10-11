using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;

namespace SimpleLyrics.Probe;

/// <summary>
/// P0 验证工具，对应 PLAN.md §5。
/// </summary>
/// <remarks>
/// 回答四个问题：
///   1. Windows 内置解码器能否解码目标格式（假设 A）；
///   2. 解码器报告的时长有多不可靠（对照完整解码计数）；
///   3. 解码器的 seek 精度是否满足打轴要求（±10ms）；
///   4. 上述分析算法本身是否正确（用 ACM 路径做对照自检）。
/// 在 Visual Studio 中设为启动项目后直接 F5 即可，无需命令行参数。
/// </remarks>
internal static class Program
{
    /// <summary>v1 必须支持的格式。</summary>
    private static readonly string[] Required = ["test.mp3", "test.flac", "test.aac"];

    /// <summary>仅作兼容性参考（Windows 无内置 Vorbis 解码器）。</summary>
    private static readonly string[] Reference = ["test.ogg"];

    /// <summary>相关系数达到此值即视为可信匹配。</summary>
    private const double ReliableCorrelation = 0.90;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var root = args.Length > 0
            ? args[0]
            : FindAudioRoot(AppContext.BaseDirectory) ?? Environment.CurrentDirectory;

        Console.WriteLine("======== Simple Lyrics · P0 音频分析 ========");
        Console.WriteLine($"音频目录 : {root}");
        Console.WriteLine($"运行时   : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"系统     : {RuntimeInformation.OSDescription}");
        Console.WriteLine();

        // FFmpeg 为替代解码方案，先跑它；若可用，Media Foundation 的多数缺陷即无需再修。
        FfmpegProbe.Run(root);

        // 产品代码路径：解码 + 峰值金字塔（SimpleLyrics.Core），这些断言可完全自动化。
        PeakProbe.Run(root);

        // 播放链路：分块存储、增量解码、缓冲裁剪与反向重定位，同样不需要音频设备。
        PlaybackProbe.Run(root);

        // 歌词解析：纯文本逻辑，与音频设备、解码器、甚至与素材文件都无关。
        LyricsProbe.Run();
        LyricEditProbe.Run();

        var decodable = DiagnoseDecoding(root);
        MeasureExactDuration(root, decodable);
        MeasureSeekAccuracy(root, decodable);
        SelfCheckWithAcm(root);

        Console.WriteLine("======== 说明 ========");
        Console.WriteLine("· 「精确时长」由完整解码累计样本数得出，是唯一可信的时间基准。");
        Console.WriteLine("· seek 偏差若显著大于 10ms，说明不能依赖解码器 seek，需自行建立帧索引。");
        Console.WriteLine("· ACM 自检用于验证分析算法本身；它与 MF 结果一致才说明结论可信。");
        Console.WriteLine();
        Console.WriteLine("按任意键退出...");
        if (!Console.IsInputRedirected) Console.ReadKey(intercept: true);

        return 0;
    }

    /// <summary>第 1 步：逐个格式尝试用 Media Foundation 打开。</summary>
    private static List<string> DiagnoseDecoding(string root)
    {
        Console.WriteLine("---- 解码诊断 ----");

        var decodable = new List<string>();

        foreach (var name in Required.Concat(Reference))
        {
            var path = TestAudio.Resolve(root, name);
            if (!File.Exists(path)) { Console.WriteLine($"  {name,-12} 不存在"); continue; }

            try
            {
                using var reader = new MediaFoundationReader(path);
                var wf = reader.WaveFormat;
                Console.WriteLine($"  {name,-12} OK     {wf.SampleRate}Hz {wf.Channels}ch {wf.BitsPerSample}bit {wf.Encoding}");
                decodable.Add(name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-12} 失败   0x{ex.HResult:X8}  {DescribeError(ex.HResult)}");
            }
        }

        Console.WriteLine();
        return decodable;
    }

    /// <summary>第 2 步：对照解码器报告时长与完整解码得出的精确时长。</summary>
    private static void MeasureExactDuration(string root, List<string> decodable)
    {
        Console.WriteLine("---- 精确时长 vs 解码器报告值 ----");
        Console.WriteLine("  文件          报告时长        精确时长        误差        样本数          解码耗时    速度");

        foreach (var name in decodable)
        {
            try
            {
                using var reader = new MediaFoundationReader(TestAudio.Resolve(root, name));
                var stats = Analysis.DecodeAll(reader);
                var errorMs = (stats.ReportedDuration - stats.ExactDuration).TotalMilliseconds;

                Console.WriteLine(
                    $"  {name,-12}  {Fmt(stats.ReportedDuration)}   {Fmt(stats.ExactDuration)}   " +
                    $"{errorMs,8:F0} ms  {stats.MonoSamples,12:N0}   {stats.Elapsed.TotalSeconds,7:F2} s  {stats.SpeedRatio,6:F0}x");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-12}  分析失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>第 3 步：用波形指纹匹配测 Media Foundation 的 seek 偏差。</summary>
    private static void MeasureSeekAccuracy(string root, List<string> decodable)
    {
        Console.WriteLine("---- seek 精度：Media Foundation ----");

        foreach (var name in decodable)
        {
            Console.WriteLine($"  [{name}]");

            try
            {
                using var reader = new MediaFoundationReader(TestAudio.Resolve(root, name));
                PrintSeekSamples(Analysis.TestSeek(reader));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>第 4 步：用 ACM 路径跑同一套分析，验证算法本身没有问题。</summary>
    private static void SelfCheckWithAcm(string root)
    {
        Console.WriteLine("---- 算法自检：ACM 路径（MP3） ----");

        var path = TestAudio.Resolve(root, "test.mp3");
        if (!File.Exists(path)) { Console.WriteLine("  test.mp3 不存在"); Console.WriteLine(); return; }

        try
        {
            using (var reader = new Mp3FileReader(path))
            {
                var stats = Analysis.DecodeAll(reader);
                var errorMs = (stats.ReportedDuration - stats.ExactDuration).TotalMilliseconds;
                Console.WriteLine(
                    $"  精确时长 {Fmt(stats.ExactDuration)}   报告时长 {Fmt(stats.ReportedDuration)}   误差 {errorMs:F0} ms   {stats.SpeedRatio:F0}x");
            }

            DiagnoseSeekReads(path);

            using (var reader = new Mp3FileReader(path))
            {
                PrintSeekSamples(Analysis.TestSeek(reader, trace: Console.WriteLine));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失败: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static void PrintSeekSamples(IReadOnlyList<SeekSample> samples)
    {
        if (samples.Count == 0) { Console.WriteLine("    无结果"); return; }

        foreach (var sample in samples)
        {
            var flag = sample.Correlation >= ReliableCorrelation ? ' ' : '?';
            Console.WriteLine(
                $"  {flag}  请求 {Fmt(sample.Target)}  →  实际 {Fmt(sample.Actual)}   偏差 {sample.ErrorMs,7:F0} ms   相关 {sample.Correlation:F3}");
        }

        var reliable = samples.Where(s => s.Correlation >= ReliableCorrelation).ToList();
        if (reliable.Count == 0)
        {
            Console.WriteLine($"    无可信样本（相关系数均低于 {ReliableCorrelation:F2}）");
            return;
        }

        var maxAbs = reliable.Max(s => Math.Abs(s.ErrorMs));
        var avgAbs = reliable.Average(s => Math.Abs(s.ErrorMs));
        Console.WriteLine(
            $"    可信样本 {reliable.Count}/{samples.Count}: 最大 |偏差| = {maxAbs:F0} ms, 平均 |偏差| = {avgAbs:F0} ms");
    }

    /// <summary>诊断 seek 之后能否正常读满探针窗口，对比「全新 reader」与「读完一遍后」的差异。</summary>
    private static void DiagnoseSeekReads(string path)
    {
        using var reader = new Mp3FileReader(path);
        var waveFormat = reader.WaveFormat;
        var windowBytes = Math.Max(
            1, waveFormat.SampleRate * 10 / 1000 * waveFormat.Channels * (waveFormat.BitsPerSample / 8));

        Console.WriteLine($"  [读取诊断] 每窗口 {windowBytes} 字节（10ms），每次尝试读 20 窗口");

        Console.WriteLine("    阶段 A：全新 reader，直接 seek");
        ProbeSeekReads(reader, windowBytes);

        Console.WriteLine("    阶段 B：先完整读完一遍（模拟指纹构建后的状态），再 seek");
        reader.Position = 0;
        var drain = new byte[1 << 16];
        while (reader.Read(drain, 0, drain.Length) > 0)
        {
            // 故意读空整个文件
        }
        ProbeSeekReads(reader, windowBytes);
    }

    private static void ProbeSeekReads(Mp3FileReader reader, int windowBytes)
    {
        foreach (var seconds in new[] { 29.37, 58.74, 88.11, 117.49, 205.60 })
        {
            reader.CurrentTime = TimeSpan.FromSeconds(seconds);

            var buffer = new byte[windowBytes * 20];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = reader.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }

            Console.WriteLine($"      请求 {seconds,8:F2}s  读到 {total / windowBytes,3} 窗口");
        }
    }

    private static string Fmt(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff");

    private static string DescribeError(int hresult) => hresult switch
    {
        unchecked((int)0xC00D36C4) => "MF_E_UNSUPPORTED_BYTESTREAM_TYPE（无对应解码器）",
        unchecked((int)0x80070005) => "E_ACCESSDENIED（环境拒绝，非格式问题）",
        _ => "未知",
    };

    /// <summary>
    /// 从给定目录向上查找仓库根，便于在 VS 中直接运行。
    /// 判据是 <c>third_party</c> 目录（FFmpeg 库所在）或仓库根的 <c>test.mp3</c>；
    /// 测试素材也可能被放在 <c>samples/</c> 子目录，由 <see cref="TestAudio"/> 统一解析。
    /// </summary>
    private static string? FindAudioRoot(string start)
    {
        var dir = new DirectoryInfo(start);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "third_party"))
                || File.Exists(Path.Combine(dir.FullName, "test.mp3")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
