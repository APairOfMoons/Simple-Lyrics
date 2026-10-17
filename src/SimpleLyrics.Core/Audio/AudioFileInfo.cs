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
/// <param name="BitRate">
/// <b>音频流的平均码率</b>（bit/s），由该流全部包的字节数除以时长算出；未知时为 0。
/// 刻意不用容器或解码器上报的那个值——它对裸流是探测阶段的估算、对 MP4 里的 AAC 是
/// 容器写的标称值，两者都可能与真实平均值差出一截（见 <c>FfmpegDecoder.MeasureAverageBitRate</c>）。
/// </param>
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

    /// <summary>
    /// 一行摘要，用于界面展示：「AAC / M4A · 44.1 kHz · 立体声 · 259 kbps」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 编解码器与容器同名时只写一个（<c>MP3 / mp3</c> 这种重复读起来像是出了错）。
    /// </para>
    /// <para>
    /// 采样率写 <c>44.1 kHz</c> 而不是 <c>44100 Hz</c>、声道写「立体声」而不是 <c>2 ch</c>：
    /// 这一行是给人看的，而那两种写法在日常语境里更常见。真有非立体声的源（单声道、
    /// 多声道）才退回数字，因为「单声道」只在 1 声道时才对，多于两个声道又说不清是几个。
    /// </para>
    /// </remarks>
    public string Summary
    {
        get
        {
            var same = string.Equals(CodecName, ContainerName, StringComparison.OrdinalIgnoreCase);

            var text = same
                ? CodecName.ToUpperInvariant()
                : $"{CodecName.ToUpperInvariant()} / {ContainerName.ToUpperInvariant()}";

            text += $" · {SampleRate / 1000.0:0.###} kHz";
            text += Channels switch
            {
                1 => " · 单声道",
                2 => " · 立体声",
                _ => $" · {Channels} 声道",
            };

            if (BitRate > 0) text += $" · {Math.Round(BitRate / 1000.0)} kbps";

            return text;
        }
    }
}
