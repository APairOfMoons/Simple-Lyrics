namespace SimpleLyrics.Core.Audio;

/// <summary>
/// 解码输出的 PCM 格式。
/// </summary>
/// <remarks>
/// 目前只支持 32 位浮点交错（<c>AV_SAMPLE_FMT_FLT</c>）一种采样格式：
/// 它是 FFmpeg 的通用交换格式，重采样到它的开销低，且浮点范围天然是 [-1, 1]，
/// 峰值与播放都不需要再做整数换算。
/// </remarks>
/// <param name="SampleRate">采样率（Hz）。</param>
/// <param name="Channels">声道数。</param>
public readonly record struct PcmFormat(int SampleRate, int Channels)
{
    /// <summary>交错缓冲中每帧的浮点数个数，即声道数。</summary>
    public int SamplesPerFrame => Channels;

    /// <summary>交错缓冲中每帧的字节数。</summary>
    public int BlockAlign => Channels * sizeof(float);

    public override string ToString() => $"{SampleRate} Hz / {Channels} ch / float32";
}
