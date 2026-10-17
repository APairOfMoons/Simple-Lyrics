namespace SimpleLyrics.Core.Lyrics.Embedded;

/// <summary>
/// 把歌词写进音频文件自己的标签里（USLT / Vorbis LYRICS / ©lyr）。
/// </summary>
/// <remarks>
/// <para>
/// <b>尚未实现。</b>它现在存在的意义是占住那个位置：保存的路由（
/// <see cref="ILyricSink"/>）与「主去处」的语义都已经按「可能有多个去处」写好了，
/// 将来实现时不必再去动保存流程。
/// </para>
/// <para>
/// <b>实现它时要守的三条</b>（现在写下来，免得到时候忘）：<br/>
/// ① <b>读—改—写，而不是新建。</b>音频里原有的其他标签必须原样保留，只替换歌词那一个；
/// ② <b>写临时文件再原子替换。</b>直接改原文件时任何一步失败都会留下一个坏掉的音频，
/// 而用户手里往往只有这一份；<br/>
/// ③ <b>动手之前问一次。</b>修改的是音频文件本身，与写一个旁边的 <c>.lrc</c> 完全不是一回事。
/// </para>
/// </remarks>
public sealed class EmbeddedLyricSink : ILyricSink
{
    public EmbeddedLyricSink(string audioPath) => AudioPath = audioPath;

    /// <summary>歌词要写进哪个音频文件。</summary>
    public string AudioPath { get; }

    public string Describe => "音频内嵌标签";

    public bool CanWrite => false;

    public string? UnavailableReason => "尚未实现 —— 计划写 USLT / Vorbis LYRICS / ©lyr（v2）";

    public long Write(string text) =>
        throw new NotSupportedException("音频内嵌歌词尚未实现；调用前请先看 CanWrite。");
}
