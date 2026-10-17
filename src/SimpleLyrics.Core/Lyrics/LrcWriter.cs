using System.Text;

namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 把一份歌词渲染成 <c>.lrc</c> 文本。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里只负责「内容」，不负责「写到哪儿」。</b>去处是 <see cref="ILyricSink"/> 的事
/// （现在有 <see cref="LrcFileSink"/>，将来还有音频内嵌标签），保存点与主去处是
/// <see cref="LyricSession"/> 的事。分家的理由很实在：写一个文件与写进音频容器
/// 完全不是一回事，而「写到哪儿」这个问题的答案不该影响「写什么」。
/// </para>
/// <para>
/// 于是「从零创建歌词」在数据层依然没有任何特殊分支：第一次保存时由界面层问一次路径
/// （建议名见 <see cref="SuggestPath"/>），拿到之后就有了主去处，两条路合成一条。
/// </para>
/// </remarks>
public static class LrcWriter
{
    /// <summary>
    /// 写出时的换行。固定 <c>CRLF</c>：这份文件的读者多半是 Windows 上的播放器与记事本。
    /// </summary>
    public const string LineBreak = "\r\n";

    /// <summary>渲染成完整的 LRC 文本。</summary>
    /// <remarks>
    /// <para>
    /// <b>只写已经打了轴的行，按时间升序。</b>没有时刻的正文在这份格式里不构成一句歌词——
    /// LRC 的一行歌词，意思就是「到了某个时刻唱这句」，播放器读到一行没有时间戳的正文
    /// 要么忽略、要么当成损坏的内容。而未打轴的行恰恰是编辑过程的中间态：粘贴进来的歌词
    /// 全都还没有时刻，得一行一行盖上去；把一行移出时间轴（回到「未绑定」栏）也是同样的意思——
    /// 那一行现在<b>不属于</b>这份文件了。于是「写出去的就是打好的轴」这条规则同时管住了两头，
    /// 不必在写之前另外判一次「这一行算不算数」。
    /// </para>
    /// <para>
    /// 写的是 <see cref="LyricDocument.Lines"/> 的一个<b>副本视图</b>，不改动文档本身——
    /// 它保持文件原序，那是界面的工作顺序。之所以在输出时排序：<c>.lrc</c> 是给别人
    /// （播放器、别的编辑器、人眼）读的，而拖动微调之后文件里必然出现乱序；
    /// 读回来的顺序由解析器恢复，工作顺序并不丢。同一时刻的多行保持文件原序
    /// （<c>OrderBy</c> 是稳定排序）。
    /// </para>
    /// <para>
    /// 刻意<b>不用</b> <see cref="LyricDocument.TimedLines"/> 那个带缓存的派生视图：
    /// 本函数是纯的，而缓存是否新鲜取决于调用方有没有在改完时间码之后
    /// <see cref="LyricDocument.Invalidate"/>。排序本身很便宜（一份歌词几百行），
    /// 用一次干净的重排换来「纯函数名副其实」，这笔买卖划算。
    /// </para>
    /// <para>
    /// 函数是纯的：同一份文档写两次得到逐字节相同的结果，所以保存是幂等的，
    /// 「写出去再读回来再写出去」也应当逐字节相同——这条由探针断言。
    /// </para>
    /// </remarks>
    public static string Format(LyricDocument document)
    {
        var builder = new StringBuilder();
        var metadata = document.Metadata;

        if (metadata.Title is { Length: > 0 } title) AppendTag(builder, "ti", title);
        if (metadata.Artist is { Length: > 0 } artist) AppendTag(builder, "ar", artist);
        if (metadata.Album is { Length: > 0 } album) AppendTag(builder, "al", album);
        if (metadata.Author is { Length: > 0 } author) AppendTag(builder, "by", author);

        // 零偏移不写：它是默认值，写一行 [offset:+0] 只会让文件多一句废话。
        if (metadata.OffsetMilliseconds != 0)
        {
            AppendTag(builder, "offset", metadata.OffsetMilliseconds.ToString("+#;-#;0", System.Globalization.CultureInfo.InvariantCulture));
        }

        // 不认识的标签原样写回（[length:] 之类）。按键名排序，好让输出可复现。
        foreach (var (key, value) in metadata.Extra.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            AppendTag(builder, key, value);
        }

        // 头部与正文之间空一行。空行在解析端是「跳过」，不承载信息，纯粹给人看。
        if (builder.Length > 0) builder.Append(LineBreak);

        var ordered = document.Lines
            .Where(static line => line.Start.HasValue)
            .OrderBy(static line => line.Start!.Value);

        foreach (var line in ordered)
        {
            builder.Append('[').Append(LrcTimecode.Format(line.Start!.Value)).Append(']');
            builder.Append(Condense(line.Text));
            builder.Append(LineBreak);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 还没有主去处时的建议保存位置：与音频同名、同目录的 <c>.lrc</c>。
    /// </summary>
    /// <remarks>
    /// 返回 <c>null</c> 表示连建议都给不出来（没打开音频）。界面层据此决定文件对话框
    /// 里预填什么名字——「与音频同名」正是侧车文件的惯例，用户多半直接回车。
    /// <see cref="LyricImport.TryLoadSidecar"/> 找侧车用的也是它，于是「自动找」与
    /// 「自动建议」永远是同一个位置。
    /// </remarks>
    public static string? SuggestPath(string? audioPath)
    {
        if (string.IsNullOrEmpty(audioPath)) return null;

        var directory = Path.GetDirectoryName(audioPath);
        var name = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrEmpty(name)) return null;

        var fileName = name + ".lrc";
        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    private static void AppendTag(StringBuilder builder, string key, string value) =>
        builder.Append('[').Append(key).Append(':').Append(value).Append(']').Append(LineBreak);

    /// <summary>
    /// 把正文里的换行压成空格。
    /// </summary>
    /// <remarks>
    /// LRC 一行就是一条歌词，正文里带换行会写出一个<b>结构上坏掉</b>的文件：
    /// 第二行会变成一条没有时间戳的正文，读回来时与原来的行再也对不上。
    /// 走正常入口进来的正文都不可能带换行——解析本身按行切，粘贴进来的文本
    /// 也先经 <see cref="PlainTextLyrics.Split"/> 切过一道。留着这一层是给
    /// 「手动分行」那类将来要直接改 <see cref="LyricLine.Text"/> 的功能兜底：
    /// 它真漏进来时，坏的是一个空格，而不是整个文件的结构。
    /// </remarks>
    private static string Condense(string text) =>
        text.Contains('\r') || text.Contains('\n')
            ? text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
            : text;
}
