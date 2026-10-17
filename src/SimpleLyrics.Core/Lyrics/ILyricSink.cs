namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 歌词可以写去的一个地方。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是接口，而不是一个枚举加 switch。</b>写 <c>.lrc</c> 文件是「打开、写、关」，
/// 十几行；而写进音频的内嵌标签是<b>读—改—写整个容器</b>：先把原有标签全部读出来、
/// 只替换歌词那一个、写进临时文件、校验、再原子替换原文件——任何一个环节失败都必须让
/// 原文件原封不动。这两件事的体量差着一个数量级，用接口的话后者自己待在一个类里，
/// 不必把它的复杂度摊到保存流程上。
/// </para>
/// <para>
/// 接口只收<b>已经渲染好的文本</b>。内容怎么生成（<see cref="LrcWriter.Format"/>，纯函数）
/// 与写到哪儿是两件事；混在一起之后，「另存为」「写进标签」这些新去处都得各自再拼一遍内容，
/// 而它们拼出来的迟早会跟主路径不一样。
/// </para>
/// <para>
/// 写失败一律<b>抛出去</b>，本接口不吞异常：调用方（<see cref="LyricSession.Save"/>）
/// 要靠「有没有抛」来决定动不动保存点，而吞掉异常会让一次失败的保存看起来像成功了。
/// </para>
/// </remarks>
public interface ILyricSink
{
    /// <summary>状态栏与对话框里怎么称呼它（「A.lrc」「音频内嵌标签」）。</summary>
    string Describe { get; }

    /// <summary>此刻写不写得下去。</summary>
    bool CanWrite { get; }

    /// <summary><see cref="CanWrite"/> 为 <c>false</c> 时说明为什么；否则为 <c>null</c>。</summary>
    string? UnavailableReason { get; }

    /// <summary>把已经渲染好的文本写出去。</summary>
    /// <returns>写出的字节数，供状态栏显示。</returns>
    long Write(string text);
}
