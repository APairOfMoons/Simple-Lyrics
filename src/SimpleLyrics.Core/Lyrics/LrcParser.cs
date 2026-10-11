using System.Globalization;
using System.Text;

namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// LRC 文本的解析。
/// </summary>
/// <remarks>
/// <para>解析规则（全部集中在 <see cref="Parse"/> 一条路径上，不依赖 UI，可单测）：</para>
/// <list type="bullet">
/// <item>一行开头可以有任意多个方括号，每个要么是 <c>[键:值]</c> 元数据，要么是时间戳。</item>
/// <item><b>一行带多个时间戳时展开成多行</b>——重复的副歌在时间轴上本来就是多个独立的位置。</item>
/// <item>既没有时间戳、又不是被方括号整行占满的内容，按「未打轴」保留。</item>
/// <item><b>空行跳过</b>：LRC 里的空行只是分隔，不承载信息，留成「未打轴的一行」只会碍事。</item>
/// <item>时间戳乱序不重排、重复时间戳不去重——文件里怎么写就怎么读，
/// 排序只发生在 <see cref="LyricDocument.TimedLines"/> 这个派生视图里。</item>
/// <item>方括号里的内容两者都不是（比如歌词正文本来就带的 <c>[笑]</c>）时，扫描到此为止，
/// <b>从它起算作歌词正文</b>、方括号原样保留；此前已经认出的时间戳照常生效。</item>
/// </list>
/// </remarks>
public static class LrcParser
{
    /// <summary>时间戳的小数部分最多几位。</summary>
    private const int MaximumFractionDigits = 3;

    /// <summary>
    /// 严格 UTF-8：遇到非法字节就抛。用来回答「这份文件到底是不是 UTF-8」，
    /// 而不是把坏字节悄悄换成 U+FFFD 之后再也分不清「文件坏了」与「歌词本来就这样」。
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 从文件读入并解析。
    /// </summary>
    /// <param name="path">.lrc 文件路径。</param>
    /// <param name="warning">
    /// 非空表示「能用，但有话要说」（目前只有「不是 UTF-8」这一种），调用方应把它显示出来。
    /// </param>
    public static LyricDocument Load(string path, out string? warning)
    {
        var text = DecodeText(File.ReadAllBytes(path), out warning);
        return Parse(text);
    }

    /// <summary>解析一段 LRC 文本。</summary>
    public static LyricDocument Parse(string text)
    {
        var document = new LyricDocument();
        if (string.IsNullOrEmpty(text)) return document;

        foreach (var rawLine in EnumerateLines(text))
        {
            ParseLine(document, rawLine);
        }

        return document;
    }

    /// <summary>
    /// 按 BOM 判断编码；没有 BOM 时先按严格 UTF-8 试一次，失败再退回 Latin-1 并给出警告。
    /// </summary>
    /// <remarks>
    /// 中文 LRC 大量产生于 GBK / ANSI 时代。这里刻意<b>不</b>引入代码页解码器
    /// （那要在运行时注册额外的编码提供程序），因为猜错的代价是不对称的：
    /// 时间戳全是 ASCII，无论怎么解码都是对的，坏掉的只有歌词正文。
    /// 「时间戳全对、中文是乱码、并且明确告诉你为什么」远比「直接报错拒绝导入」有用——
    /// 前者用户还能立刻看出是编码问题并去转存，后者他只会以为文件坏了。
    /// </remarks>
    public static string DecodeText(byte[] bytes, out string? warning)
    {
        warning = null;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            warning = "该文件不是 UTF-8 编码（多半是 GBK / ANSI）：时间戳可用，中文可能显示为乱码，"
                    + "请在记事本或 VS Code 中另存为 UTF-8 后重新导入";

            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// 逐行切分。手写而不用 <c>string.Split</c>，是为了让「<c>\r\n</c> 只算一个换行」
    /// 这件事就地确定下来，而不是依赖分隔符数组的空项行为。
    /// </summary>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;

            yield return text[start..index];

            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;

            start = index + 1;
        }

        if (start < text.Length) yield return text[start..];
    }

    private static void ParseLine(LyricDocument document, string rawLine)
    {
        var position = 0;
        List<TimeSpan>? stamps = null;

        while (position < rawLine.Length && rawLine[position] == '[')
        {
            var close = rawLine.IndexOf(']', position + 1);
            if (close < 0) break;

            var content = rawLine[(position + 1)..close];

            if (TrySplitTag(content, out var key, out var value))
            {
                ApplyTag(document, key, value);
            }
            else if (TryParseTimestamp(content, out var stamp))
            {
                (stamps ??= []).Add(stamp);
            }
            else
            {
                // 既不认识也不是时间戳，扫描到此为止。剩下的（含这个方括号）算作歌词正文。
                //
                // 这样处理同时照顾到两种真实写法：歌词正文自带的 [笑]、[合唱]，
                // 以及「时间戳 + 方括号批注」并存的行。若在这里把<b>整行</b>判为正文，
                // 后一种写法会白白丢掉一个完全合法的时间戳；而若把它当成可忽略的标记丢掉，
                // 前一种写法又会缺字。从认不出的那个方括号起算，两边都不吃亏。
                break;
            }

            position = close + 1;
        }

        var text = rawLine[position..].Trim();

        if (stamps is { Count: > 0 })
        {
            foreach (var stamp in stamps)
            {
                document.Lines.Add(new LyricLine { Text = text, Start = stamp });
            }

            return;
        }

        // 纯元数据行（[ti:…]）到此正文为空，不产生歌词行。
        if (text.Length > 0) document.Lines.Add(new LyricLine { Text = text });
    }

    /// <summary>
    /// 判断方括号里是不是 <c>键:值</c> 元数据。
    /// </summary>
    /// <remarks>
    /// 判据是「第一个冒号之前全是字母」。时间戳的冒号之前必然是数字，
    /// 因此 <c>[ti:…]</c> 与 <c>[00:12.34]</c> 不会互相误判，不必维护一张标签白名单。
    /// </remarks>
    private static bool TrySplitTag(string content, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var colon = content.IndexOf(':');
        if (colon <= 0) return false;

        var candidate = content[..colon].Trim();
        if (candidate.Length == 0) return false;

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetter(character)) return false;
        }

        key = candidate.ToLowerInvariant();
        value = content[(colon + 1)..].Trim();
        return true;
    }

    private static void ApplyTag(LyricDocument document, string key, string value)
    {
        var metadata = document.Metadata;

        switch (key)
        {
            case "ti":
                metadata.Title = value;
                break;

            case "ar":
                metadata.Artist = value;
                break;

            case "al":
                metadata.Album = value;
                break;

            case "by":
                metadata.Author = value;
                break;

            case "offset":
                if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds))
                {
                    metadata.OffsetMilliseconds = milliseconds;
                }
                break;

            default:
                metadata.Extra[key] = value;
                break;
        }
    }

    /// <summary>
    /// <c>mm:ss</c>、<c>mm:ss.xx</c>、<c>mm:ss.xxx</c> 以及 <c>hh:mm:ss.xx</c>。
    /// </summary>
    /// <remarks>
    /// 分钟位刻意不限制在 0–59：LRC 里 <c>[100:12.34]</c> 是常见写法，
    /// 分钟累积成小时算不上错误，拒绝它反而会白丢一行歌词。
    /// </remarks>
    private static bool TryParseTimestamp(string content, out TimeSpan value)
    {
        value = default;

        var parts = content.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;

        if (parts.Length == 2)
        {
            if (!TryParseInt(parts[0], out var minutes)) return false;
            if (!TryParseFractionalSeconds(parts[1], out var seconds)) return false;

            value = TimeSpan.FromSeconds(minutes * 60 + seconds);
            return true;
        }

        if (!TryParseInt(parts[0], out var hours)) return false;
        if (!TryParseInt(parts[1], out var minutes2)) return false;
        if (!TryParseFractionalSeconds(parts[2], out var seconds2)) return false;

        value = TimeSpan.FromSeconds(hours * 3600 + minutes2 * 60 + seconds2);
        return true;
    }

    /// <summary>
    /// 秒，可带小数。<c>.5</c> = 500 毫秒、<c>.55</c> = 550 毫秒、<c>.555</c> = 555 毫秒，
    /// 也就是按十进制读，而不是把小数点后的数字直接当成毫秒——后者会把 <c>[00:12.5]</c>
    /// 读成 12 秒 5 毫秒，整整偏出半秒。
    /// </summary>
    private static bool TryParseFractionalSeconds(string text, out double value)
    {
        value = 0;

        var trimmed = text.Trim();

        var separator = trimmed.IndexOf('.');
        if (separator < 0) separator = trimmed.IndexOf(',');

        var whole = separator < 0 ? trimmed : trimmed[..separator];
        var fraction = separator < 0 ? string.Empty : trimmed[(separator + 1)..];

        if (!TryParseInt(whole, out var seconds)) return false;

        var milliseconds = 0;

        if (fraction.Length > 0)
        {
            if (fraction.Length > MaximumFractionDigits) return false;
            if (!TryParseInt(fraction, out milliseconds)) return false;

            // 补齐到三位再当毫秒用：'.5' → 500。
            for (var digits = fraction.Length; digits < MaximumFractionDigits; digits++) milliseconds *= 10;
        }

        value = seconds + milliseconds / 1000.0;
        return true;
    }

    /// <summary>
    /// 只接受纯数字。用 <see cref="NumberStyles.None"/> 而不是默认的整数样式：
    /// 默认样式接受前后空白与正负号，于是 <c>[+1:-2]</c> 这种东西也会被当成时间戳，
    /// 而它显然不是。
    /// </summary>
    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
