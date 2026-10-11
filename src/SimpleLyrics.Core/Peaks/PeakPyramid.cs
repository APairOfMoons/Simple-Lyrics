namespace SimpleLyrics.Core.Peaks;

/// <summary>
/// 波形峰值金字塔：多级降采样的 <c>(min, max)</c> 序列，外加权威时长。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要金字塔</b>：整曲波形在屏幕上只有几百到几千个像素行，但一首 4 分钟的歌
/// 有上千万个采样。逐采样绘制既不可能也没意义。金字塔每向上一级就缩小 4 倍，
/// 绘制时按「每行覆盖的秒数」挑一级，使每行只需读常数个桶——
/// 于是无论缩放到哪一级，绘制耗时都恒定。
/// </para>
/// <para>
/// <b>时长以本类的 <see cref="Duration"/> 为准</b>：它由完整解码累计的样本数算出。
/// 容器声明的时长对 ADTS AAC 之类的裸流只能估算，实测偏差可近 30 秒。
/// </para>
/// <para>
/// 本类持有用于 <see cref="FillEnvelope"/> 的复用缓冲，因此**不是线程安全的**；
/// 它设计为只在 UI 线程上绘制使用。
/// </para>
/// </remarks>
public sealed class PeakPyramid
{
    private float[][]? _scratchMin;
    private float[][]? _scratchMax;

    internal PeakPyramid(int sampleRate, int channels, long totalFrames, IReadOnlyList<PeakLevel> levels)
    {
        if (levels.Count == 0) throw new ArgumentException("峰值金字塔至少需要一级", nameof(levels));

        SampleRate = sampleRate;
        Channels = channels;
        TotalFrames = totalFrames;
        Levels = levels;
    }

    /// <summary>采样率（Hz）。</summary>
    public int SampleRate { get; }

    /// <summary>声道数。</summary>
    public int Channels { get; }

    /// <summary>完整解码得到的帧数（每声道）。</summary>
    public long TotalFrames { get; }

    /// <summary>由样本数算出的权威时长。</summary>
    public TimeSpan Duration => SampleRate > 0
        ? TimeSpan.FromSeconds((double)TotalFrames / SampleRate)
        : TimeSpan.Zero;

    /// <summary>权威时长（秒）。</summary>
    public double DurationSeconds => SampleRate > 0 ? (double)TotalFrames / SampleRate : 0;

    /// <summary>金字塔各级，索引 0 为最精细的一级。</summary>
    public IReadOnlyList<PeakLevel> Levels { get; }

    /// <summary>最精细的一级。</summary>
    public PeakLevel BaseLevel => Levels[0];

    /// <summary>整个金字塔占用的字节数，用于在界面上展示内存开销。</summary>
    public long ApproximateBytes
    {
        get
        {
            long bytes = 0;
            foreach (var level in Levels) bytes += (long)level.Count * Channels * 2 * sizeof(float);
            return bytes;
        }
    }

    /// <summary>
    /// 查询某个时间区间的峰值。区间跨越的桶会被全部合并。
    /// </summary>
    /// <param name="channel">声道号。</param>
    public (float Min, float Max) Query(int channel, double startSeconds, double endSeconds)
    {
        if (channel < 0 || channel >= Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        if (endSeconds <= startSeconds) endSeconds = startSeconds + 1e-9;

        // 整段都落在音频开始之前：视为静音。
        // 这一句必须排在下面那个「起点钳到 0」之前——否则一段完全处于负区的查询会被
        // 改写成「查询 0 秒附近」，于是波形开头那一下会被错误地铺满整个超限滚动区域。
        if (endSeconds <= 0) return (0f, 0f);

        // 起点已经在音频之后：视为静音，避免把末尾那个桶拉伸到无限远。
        if (startSeconds >= DurationSeconds) return (0f, 0f);
        if (startSeconds < 0) startSeconds = 0;

        // 选「桶时长不超过查询区间」的最大一级：
        // 再细一级每个桶都会被不足一次地重复读取，再粗一级则每行要扫很多桶。
        // 上一级的桶时长是本级的 4 倍，所以选中的级别保证每行最多读约 7 个桶。
        var levelIndex = 0;
        for (var i = 1; i < Levels.Count; i++)
        {
            if (Levels[i].BucketSeconds(SampleRate) <= endSeconds - startSeconds) levelIndex = i;
            else break;
        }

        var level = Levels[levelIndex];
        var bucketSeconds = level.BucketSeconds(SampleRate);
        if (bucketSeconds <= 0) return (0f, 0f);

        var first = (int)Math.Floor(startSeconds / bucketSeconds);
        var last = (int)Math.Ceiling(endSeconds / bucketSeconds);   // 开区间上界
        if (last <= first) last = first + 1;

        first = Math.Clamp(first, 0, level.Count - 1);
        last = Math.Clamp(last, first + 1, level.Count);

        var min = float.MaxValue;
        var max = float.MinValue;
        var stride = Channels;

        for (var bucket = first; bucket < last; bucket++)
        {
            var index = bucket * stride + channel;
            if (level.Mins[index] < min) min = level.Mins[index];
            if (level.Maxs[index] > max) max = level.Maxs[index];
        }

        return min > max ? (0f, 0f) : (min, max);
    }

    /// <summary>
    /// 把每个时间片段的对称包络写入 <paramref name="destination"/>。
    /// </summary>
    /// <param name="startSeconds">首个片段（对应屏幕第 0 行）的起始时间。</param>
    /// <param name="secondsPerRow">每个片段覆盖的秒数，即 <c>1 / 每秒像素数</c>。</param>
    /// <param name="destination">
    /// <c>destination[声道][行]</c>，值域 0..1。
    /// </param>
    /// <remarks>
    /// 取 <c>max(|min|, |max|)</c> 而不是分别绘制正负：直流偏置或不对称波形会让
    /// 图形整体歪向一侧，而打轴关心的是「这里有多响」，对称包络既稳又省一半绘制量。
    /// </remarks>
    public void FillEnvelope(double startSeconds, double secondsPerRow, float[][] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < Channels) throw new ArgumentException("目标数组的声道数不足", nameof(destination));

        var rows = destination[0].Length;
        EnsureScratch(rows);
        FillMinMax(startSeconds, secondsPerRow, rows, _scratchMin!, _scratchMax!);

        for (var channel = 0; channel < Channels; channel++)
        {
            var mins = _scratchMin![channel];
            var maxs = _scratchMax![channel];
            var target = destination[channel];

            for (var row = 0; row < rows; row++)
            {
                var value = MathF.Max(MathF.Abs(mins[row]), MathF.Abs(maxs[row]));
                target[row] = value > 1f ? 1f : value;
            }
        }
    }

    /// <summary>按行填充原始的最小 / 最大值（不做对称化）。</summary>
    public void FillMinMax(double startSeconds, double secondsPerRow, int rows, float[][] minOut, float[][] maxOut)
    {
        ArgumentNullException.ThrowIfNull(minOut);
        ArgumentNullException.ThrowIfNull(maxOut);

        for (var row = 0; row < rows; row++)
        {
            var from = startSeconds + row * secondsPerRow;
            var to = from + secondsPerRow;

            for (var channel = 0; channel < Channels; channel++)
            {
                var (min, max) = Query(channel, from, to);
                minOut[channel][row] = min;
                maxOut[channel][row] = max;
            }
        }
    }

    private void EnsureScratch(int rows)
    {
        if (_scratchMin is { } existing
            && existing.Length == Channels
            && existing[0].Length == rows)
        {
            return;
        }

        _scratchMin = new float[Channels][];
        _scratchMax = new float[Channels][];
        for (var channel = 0; channel < Channels; channel++)
        {
            _scratchMin[channel] = new float[rows];
            _scratchMax[channel] = new float[rows];
        }
    }
}
