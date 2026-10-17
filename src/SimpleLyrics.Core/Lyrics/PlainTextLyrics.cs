namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 把粘贴进来的纯文本切成一行一句的歌词。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独立一处，而不是塞进 <see cref="LrcParser"/>。</b>解析 <c>.lrc</c> 要处理时间戳、
/// 标签、多时间戳展开这一整套语法；而这里要做的事恰好相反——<b>假定文本里没有任何语法</b>，
/// 一行就是一句歌词。两者的规则没有一句是共用的，混在一起只会让「这行到底会不会被当成
/// 时间戳」变成需要翻两处代码才答得上来的问题。
/// </para>
/// <para>
/// 将来要实现「手动分行」（把一行长句切成两行、或把两行并成一行）时，入口也在这一层：
/// 那时它会多出「按标点建议断点」之类的规则，而 <see cref="LrcParser"/> 一个字都不用动。
/// </para>
/// </remarks>
public static class PlainTextLyrics
{
    /// <summary>
    /// 按行切开，去掉每行首尾的空白，丢掉空行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三种换行（<c>CRLF</c> / <c>LF</c> / <c>CR</c>）一视同仁：粘贴来的文本可能来自记事本、
    /// 也可能是从网页或聊天窗口里复制的，分隔符是什么完全不可控。按单个字符切、再让
    /// 「连续的换行」自然产生空条目，比先做一次规范化替换少一步且不会漏。
    /// </para>
    /// <para>
    /// <b>空行直接丢掉，不留成空歌词。</b>它们在这份文本里只是段落之间的分隔，
    /// 而留下的代价很实在：每一行都要占一次打轴（用户得为空行也按一次盖章），
    /// 写出去还会在文件里多出一行没有正文的时间戳。
    /// </para>
    /// <para>
    /// 行<b>内部</b>的空白原样保留。歌词里的空格常常是有意为之（比如英文对唱的空位、
    /// 用空格对齐的合唱段落），压缩它们等于替用户改词。
    /// </para>
    /// </remarks>
    /// <returns>行的正文，顺序与文本一致。</returns>
    public static List<string> Split(string? text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        // 从别处复制来的文本可能带着 BOM——它是「这个文件是 UTF-8」的标记，
        // 粘进来之后就成了正文的一部分，而它不可见，会让第一行永远对不上。
        foreach (var raw in text.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0) lines.Add(line);
        }

        return lines;
    }
}
