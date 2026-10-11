using System.Text;

namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 把一份歌词写回 <c>.lrc</c> 文本。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是「保存」这件事的唯一出口</b>，有源文件与没有源文件的歌词都走它——
/// 两者的区别只在<b>路径从哪儿来</b>：有源文件时路径就是
/// <see cref="LyricDocument.SourcePath"/>，没有时由界面层问用户要一个（见
/// <see cref="SuggestPath"/>），拿到之后又变回「有源文件」。于是「从零创建歌词」
/// 这件事在数据层没有任何特殊分支：第一次保存之后，两条路就合成了一条。
/// </para>
/// <para>
/// 因此 <see cref="Save"/> 顺手把 <see cref="LyricDocument.SourcePath"/> 指过去、
/// 把 <see cref="LyricDocument.IsDirty"/> 清掉。这两件事放在这里而不是各个调用点，
/// 是因为任何一条漏掉都会造成「明明保存过了却还标着未保存」，或者反过来的静默覆盖。
/// </para>
/// </remarks>
public static class LrcWriter
{
    /// <summary>
    /// 写出时的换行。固定 <c>CRLF</c>：这份文件的读者多半是 Windows 上的播放器与记事本。
    /// </summary>
    public const string LineBreak = "\r\n";

    /// <summary>
    /// 写出时的编码：UTF-8 <b>带 BOM</b>。
    /// </summary>
    /// <remarks>
    /// 中文 LRC 的读者里有相当一部分是按 BOM 判断编码的；不带 BOM 时它们会去猜 GBK，
    /// 整份中文就花了。本工具自己的解析器两种都能读（见 <see cref="LrcParser.DecodeText"/>），
    /// 所以带 BOM 只会带来兼容性，不带来风险。
    /// </remarks>
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>渲染成完整的 LRC 文本。</summary>
    /// <remarks>
    /// <para>
    /// <b>已打轴的行按时间升序写，未打轴的行排在最后。</b>写的是 <see cref="LyricDocument.Lines"/>
    /// 的一个<b>副本视图</b>，不改动文档本身——它保持文件原序，那是界面的工作顺序。
    /// 之所以在输出时排序：<c>.lrc</c> 是给别人（播放器、别的编辑器、人眼）读的，
    /// 而拖动微调之后文件里必然出现乱序；读回来的顺序由解析器恢复，工作顺序并不丢。
    /// 同一时刻的多行保持文件原序（<c>OrderBy</c> 是稳定排序）。
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
            .OrderBy(static line => line.Start!.Value)
            .Concat(document.Lines.Where(static line => !line.Start.HasValue));

        foreach (var line in ordered)
        {
            if (line.Start is { } start)
            {
                builder.Append('[').Append(LrcTimecode.Format(start)).Append(']');
            }

            builder.Append(Condense(line.Text));
            builder.Append(LineBreak);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把歌词落盘。
    /// </summary>
    /// <returns>写出的字节数，供状态栏显示。</returns>
    public static long Save(LyricDocument document, string path)
    {
        var text = Format(document);
        File.WriteAllText(path, text, Utf8WithBom);

        document.SourcePath = path;
        document.IsDirty = false;

        return Utf8WithBom.GetByteCount(text) + Utf8WithBom.GetPreamble().Length;
    }

    /// <summary>
    /// 还没有源文件时的建议保存位置：与音频同名、同目录的 <c>.lrc</c>。
    /// </summary>
    /// <remarks>
    /// 返回 <c>null</c> 表示连建议都给不出来（没打开音频）。界面层据此决定文件对话框
    /// 里预填什么名字——「与音频同名」正是侧车文件的惯例，用户多半直接回车。
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
    /// 目前解析出来的正文都不可能带换行（解析本身按行切），这一层是给将来
    /// 「粘贴多行歌词」准备的——那时换行会被当成行的分隔，而不是落进正文里。
    /// </remarks>
    private static string Condense(string text) =>
        text.Contains('\r') || text.Contains('\n')
            ? text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
            : text;
}
