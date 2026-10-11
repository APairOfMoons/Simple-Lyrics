namespace SimpleLyrics.Core.Peaks;

/// <summary>
/// 峰值金字塔中的一级：把若干个连续帧压缩成一个 <c>(最小值, 最大值)</c> 桶。
/// </summary>
/// <remarks>
/// <para>
/// 多声道按 <c>[桶序号 * Channels + 声道号]</c> 交错存放——绘制时需要按时间顺序
/// 连续读取同一声道的数据，交错布局比「每声道一个数组」对缓存更友好。
/// </para>
/// <para>
/// 用 min/max 而不是 RMS 或平均：打轴时最需要看到的是瞬态（辅音、鼓点）的位置，
/// 平均值会把它们抹平。
/// </para>
/// </remarks>
public sealed class PeakLevel
{
    internal PeakLevel(int bucketFrames, int channels, float[] mins, float[] maxs)
    {
        BucketFrames = bucketFrames;
        Channels = channels;
        Mins = mins;
        Maxs = maxs;
        Count = channels > 0 ? mins.Length / channels : 0;
    }

    /// <summary>一个桶覆盖的帧数（每声道）。</summary>
    public int BucketFrames { get; }

    /// <summary>声道数。</summary>
    public int Channels { get; }

    /// <summary>桶的数量。</summary>
    public int Count { get; }

    /// <summary>每个桶的最小值，长度 <c>Count * Channels</c>。</summary>
    public float[] Mins { get; }

    /// <summary>每个桶的最大值，长度 <c>Count * Channels</c>。</summary>
    public float[] Maxs { get; }

    /// <summary>本级的桶在给定采样率下覆盖的秒数。</summary>
    public double BucketSeconds(int sampleRate) => sampleRate > 0 ? (double)BucketFrames / sampleRate : 0;
}
