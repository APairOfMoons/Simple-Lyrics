using SimpleLyrics.Core.Audio;
using SimpleLyrics.Core.Peaks;

namespace SimpleLyrics.Probe;

/// <summary>
/// 峰值金字塔的端到端验证：确认 <see cref="PeakGenerator"/> 与 <see cref="PeakPyramid"/>
/// 产出的波形数据真的能代表原始音频。
/// </summary>
/// <remarks>
/// <para>
/// 这是界面之外唯一能自动化验证的一环，因此验证的是**不变量**而不是实现细节：
/// </para>
/// <list type="bullet">
/// <item><b>包络不漏</b>：整曲均匀撒 200 个互不重叠的时间窗，独立解码收集窗内每个原始样本，
/// 金字塔对该窗的查询结果必须**完全包住**这些样本。若时间映射、采样率或声道交错有任何错位，
/// 这个断言必然失败——它是波形正确性的最强证据。</item>
/// <item><b>声道不串</b>：立体声素材的左右声道统计量应当有差异，全等说明交错写错了。</item>
/// <item><b>界面入口合法</b>：<see cref="PeakPyramid.FillEnvelope"/> 是绘制时唯一的取数入口，
/// 其输出必须落在 0..1 且无 NaN。</item>
/// </list>
/// </remarks>
internal static class PeakProbe
{
    /// <summary>撒点数量。</summary>
    private const int WindowCount = 200;

    /// <summary>每个窗口的半宽，取 20 ms 以便容纳足够多的样本。</summary>
    private const double WindowHalfSeconds = 0.02;

    /// <summary>浮点比较容差。</summary>
    private const float Epsilon = 1e-6f;

    private static readonly (string File, string Label)[] Targets =
    [
        ("test.mp3", "MP3"),
        ("test.flac", "FLAC"),
        ("test.aac", "AAC(ADTS)"),
        ("test.ogg", "OGG/Vorbis"),
    ];

    public static void Run(string repositoryRoot)
    {
        Console.WriteLine("---- 峰值金字塔（Core / PeakGenerator） ----");
        Console.WriteLine($"  每个文件撒 {WindowCount} 个时间窗独立解码取样，检验波形包络是否包住全部原始样本。");
        Console.WriteLine();

        foreach (var (file, label) in Targets)
        {
            var path = TestAudio.Resolve(repositoryRoot, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"  [{label}] {file} 不存在");
                continue;
            }

            try
            {
                Inspect(label, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{label}] 失败：{ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine();
        }
    }

    private static void Inspect(string label, string path)
    {
        var result = PeakGenerator.Generate(path);
        var pyramid = result.Pyramid;

        Console.WriteLine($"  [{label}] {result.Info.Summary}");
        Console.WriteLine(
            $"      权威时长 {Format(pyramid.Duration)}   {pyramid.TotalFrames:N0} 帧"
            + $"   首开耗时 {result.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine(
            $"      金字塔 {pyramid.Levels.Count} 级："
            + string.Join(" → ", pyramid.Levels.Select(level => level.Count.ToString("N0"))));
        Console.WriteLine(
            $"      峰值内存 {pyramid.ApproximateBytes / 1024.0:F0} KB"
            + $"（约 {pyramid.ApproximateBytes / Math.Max(pyramid.DurationSeconds, 1) / 1024.0:F1} KB/秒）");

        Console.WriteLine($"      {VerifyEnvelope(path, pyramid)}");
        Console.WriteLine($"      {FillProbe(pyramid)}");
    }

    /// <summary>
    /// 撒点验证：金字塔的查询结果必须包住窗口内的全部原始样本。
    /// </summary>
    private static string VerifyEnvelope(string path, PeakPyramid pyramid)
    {
        var duration = pyramid.DurationSeconds;
        if (duration <= 0) return "包络校验：跳过（时长为 0）";

        // 窗口等距且互不重叠：间距 = 时长 / 窗口数，窗宽 40ms 远小于间距（4 分钟素材约 1.2s）。
        var spacing = duration / WindowCount;
        var windows = new Window[WindowCount];
        for (var i = 0; i < WindowCount; i++)
        {
            var center = (i + 0.5) * spacing;
            windows[i] = new Window(
                Math.Max(0, center - WindowHalfSeconds),
                Math.Min(duration, center + WindowHalfSeconds));
        }

        using (var decoder = FfmpegDecoder.Open(path, outputChannels: 2))
        {
            decoder.DecodeTo(new WindowCollector(windows, decoder.OutputFormat));
        }

        var violations = 0;
        var overflow = 0;
        var worstRedundancy = 0f;
        var stereoSum = 0.0;
        var stereoCount = 0;

        foreach (var window in windows)
        {
            if (!window.Observed)
            {
                overflow++;
                continue;
            }

            for (var channel = 0; channel < pyramid.Channels; channel++)
            {
                var (queryMin, queryMax) = pyramid.Query(channel, window.From, window.To);
                var exactMin = window.Min[channel];
                var exactMax = window.Max[channel];

                if (queryMin > exactMin + Epsilon || queryMax < exactMax - Epsilon)
                {
                    violations++;
                    if (violations <= 3)
                    {
                        Console.WriteLine(
                            $"        × {window.From:F3}s 声道{channel}："
                            + $"精确 [{exactMin:F4}, {exactMax:F4}] 未被 [{queryMin:F4}, {queryMax:F4}] 包住");
                    }
                }

                // 冗余 = 包络比实际宽出多少。桶边界会带来少量冗余，但不应达到信号本身的量级。
                var redundancy = MathF.Max(0f, MathF.Max(exactMin - queryMin, queryMax - exactMax));
                if (redundancy > worstRedundancy) worstRedundancy = redundancy;
            }

            if (pyramid.Channels > 1)
            {
                stereoSum += Math.Abs(window.Max[0] - window.Max[1]);
                stereoCount++;
            }
        }

        var observed = WindowCount - overflow;
        var verdict = violations == 0 ? "✓ 全部包住" : $"× {violations} 处漏检";

        string stereo;
        if (stereoCount == 0)
        {
            stereo = "单声道（无左右之分）";
        }
        else
        {
            var average = stereoSum / stereoCount;
            var comment = average > 1e-4 ? "左右有差异，声道分离正常" : "左右几乎全等，可疑";
            stereo = $"{average:F4}（{comment}）";
        }

        return $"包络校验：{observed}/{WindowCount} 窗有效，{verdict}，最大冗余 {worstRedundancy:F4}；"
             + $"左右声道平均峰值差 {stereo}";
    }

    /// <summary>验证界面取数入口的输出是否合法。</summary>
    private static string FillProbe(PeakPyramid pyramid)
    {
        const int rows = 400;

        var envelope = new float[pyramid.Channels][];
        for (var channel = 0; channel < pyramid.Channels; channel++) envelope[channel] = new float[rows];

        pyramid.FillEnvelope(0, pyramid.DurationSeconds / rows, envelope);

        var invalid = 0;
        var peak = 0f;
        var nonZero = 0;

        foreach (var channel in envelope)
        {
            foreach (var value in channel)
            {
                if (float.IsNaN(value) || value < 0f || value > 1f) invalid++;
                if (value > peak) peak = value;
                if (value > 0.001f) nonZero++;
            }
        }

        var total = rows * pyramid.Channels;
        return invalid == 0
            ? $"FillEnvelope：{rows} 行 × {pyramid.Channels} 声道全部合法，峰值 {peak:F3}，非静音行 {nonZero * 100 / total}% ✓"
            : $"FillEnvelope：出现 {invalid} 个非法值 ×";
    }

    private static string Format(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff");

    /// <summary>一个取样时间窗及其独立收集到的精确极值。</summary>
    private sealed class Window(double from, double to)
    {
        public double From { get; } = from;
        public double To { get; } = to;

        public float[] Min { get; } = [float.MaxValue, float.MaxValue];
        public float[] Max { get; } = [float.MinValue, float.MinValue];

        /// <summary>窗内是否真的取到了样本（文件尾部可能落在时长之外）。</summary>
        public bool Observed { get; set; }
    }

    /// <summary>
    /// 独立于 <see cref="PeakGenerator"/> 的取样器：按帧号把样本归入所属时间窗，
    /// 收集精确的 min/max。窗口按时间有序且互不重叠，所以只需一个游标推进。
    /// </summary>
    private sealed class WindowCollector : IPcmSink
    {
        private readonly Window[] _windows;
        private readonly int _channels;
        private readonly long[] _firstFrame;
        private readonly long[] _lastFrame;

        private int _cursor;
        private long _frame;

        public WindowCollector(Window[] windows, PcmFormat format)
        {
            _windows = windows;
            _channels = format.Channels;
            _firstFrame = windows.Select(window => (long)(window.From * format.SampleRate)).ToArray();
            _lastFrame = windows.Select(window => (long)(window.To * format.SampleRate)).ToArray();
        }

        public void Write(ReadOnlySpan<float> samples, int frameCount)
        {
            for (var index = 0; index < frameCount; index++)
            {
                while (_cursor < _windows.Length && _frame > _lastFrame[_cursor]) _cursor++;
                if (_cursor >= _windows.Length) return;   // 后面的样本都超出最后一个窗口了

                if (_frame >= _firstFrame[_cursor])
                {
                    var window = _windows[_cursor];
                    window.Observed = true;

                    var offset = index * _channels;
                    for (var channel = 0; channel < _channels; channel++)
                    {
                        var value = samples[offset + channel];
                        if (value < window.Min[channel]) window.Min[channel] = value;
                        if (value > window.Max[channel]) window.Max[channel] = value;
                    }
                }

                _frame++;
            }
        }
    }
}
