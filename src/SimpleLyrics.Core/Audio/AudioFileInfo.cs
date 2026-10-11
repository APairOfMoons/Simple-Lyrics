namespace SimpleLyrics.Core.Audio;

/// <summary>
/// 打开音频文件后立刻可以拿到的元信息（无需完整解码）。
/// </summary>
/// <param name="Path">文件绝对路径。</param>
/// <param name="CodecName">FFmpeg 报告的编解码器短名，如 <c>mp3</c> / <c>flac</c> / <c>aac</c>。</param>
/// <param name="ContainerName">容器格式短名，如 <c>mp3</c> / <c>flac</c> / <c>aac</c> / <c>ogg</c>。</param>
/// <param name="SampleRate">解码器采样率（Hz）。</param>
/// <param name="Channels">源文件声道数。</param>
/// <param name="ContainerDuration">
/// 容器声明的时长。**不可作为时间轴基准**：
/// ADTS AAC 没有时长字段，容器只能按码率估算，实测偏差近 30 秒。
/// 权威时长请用 <see cref="Peaks.PeakPyramid.Duration"/>（完整解码累计样本数得出）。
/// </param>
/// <param name="BitRate">码率（bit/s），未知时为 0。</param>
public sealed record AudioFileInfo(
    string Path,
    string CodecName,
    string ContainerName,
    int SampleRate,
    int Channels,
    TimeSpan ContainerDuration,
    long BitRate)
{
    /// <summary>容器时长是否可信。ADTS 之类的裸流会给出明显错误的估算值。</summary>
    public bool ContainerDurationIsReliable => ContainerDuration > TimeSpan.Zero;

    /// <summary>一行摘要，用于界面展示。</summary>
    public string Summary =>
        $"{CodecName} / {ContainerName} · {SampleRate} Hz · {Channels} ch"
        + (BitRate > 0 ? $" · {BitRate / 1000} kbps" : string.Empty);
}
