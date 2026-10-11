using System.Text;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Probe;

/// <summary>
/// LRC 解析的边界用例，对应 PLAN.md §9。
/// </summary>
/// <remarks>
/// 歌词解析是纯逻辑，没有文件系统以外的依赖、也没有 UI，本来就该由单元测试来管。
/// 测试项目尚未获准引入测试框架（见 PLAN §6），所以先挂在探针里；
/// 等 <c>tests/SimpleLyrics.Core.Tests</c> 立项之后，这个文件可以整份平移过去。
/// </remarks>
internal static class LyricsProbe
{
    public static void Run()
    {
        Checks.Reset();

        Console.WriteLine("---- LRC 解析 ----");

        CheckTimestampFormats();
        CheckMetadata();
        CheckMultipleTimestamps();
        CheckUntimedAndBlankLines();
        CheckOrderAndDuplicates();
        CheckBracketedText();
        CheckMalformed();
        CheckEncoding();

        Console.WriteLine($"  合计 {Checks.Passed} 通过, {Checks.Failed} 失败");
        Console.WriteLine();
    }

    private static void CheckTimestampFormats()
    {
        var document = LrcParser.Parse(
            """
            [00:12.34]两位小数
            [00:05]没有小数
            [00:05.5]一位小数
            [00:05.555]三位小数
            [100:12.34]分钟超过 59
            [1:02:03.45]带小时
            """);

        // 刻意读 Lines（文件原序）而不是 TimedLines：这一组断言既查格式，也顺带查
        // 「解析不重排」——若读 TimedLines，排序会先把顺序改掉，断言就永远对不上。
        var times = document.Lines;

        Equal("行数", 6, times.Count);
        Equal("mm:ss.xx", TimeSpan.FromSeconds(12.34), times[0].Start);
        Equal("mm:ss", TimeSpan.FromSeconds(5), times[1].Start);
        Equal("mm:ss.f 按十进制读（.5 = 500ms）", TimeSpan.FromSeconds(5.5), times[2].Start);
        Equal("mm:ss.fff", TimeSpan.FromSeconds(5.555), times[3].Start);
        Equal("分钟位不限于 0–59", TimeSpan.FromSeconds(100 * 60 + 12.34), times[4].Start);
        Equal("hh:mm:ss.xx", TimeSpan.FromSeconds(3723.45), times[5].Start);
    }

    private static void CheckMetadata()
    {
        var document = LrcParser.Parse(
            """
            [ti:测试标题]
            [ar:某位歌手]
            [al:某张专辑]
            [by:某人]
            [offset:+250]
            [length:03:54]
            [00:01.00]第一行
            """);

        Equal("ti", "测试标题", document.Metadata.Title);
        Equal("ar", "某位歌手", document.Metadata.Artist);
        Equal("al", "某张专辑", document.Metadata.Album);
        Equal("by", "某人", document.Metadata.Author);
        Equal("offset 带正号", 250, document.Metadata.OffsetMilliseconds);
        Equal("不认识的标签原样保留", "03:54", document.Metadata.Extra.GetValueOrDefault("length"));

        // 纯元数据行不该变成歌词行，否则未打轴栏里会冒出一堆 [ti:…]。
        Equal("元数据行不产生歌词行", 1, document.Lines.Count);

        // offset 刻意不折算，理由见 LrcMetadata.OffsetMilliseconds。
        Equal("offset 不折算进行时间", TimeSpan.FromSeconds(1), document.TimedLines[0].Start);
    }

    private static void CheckMultipleTimestamps()
    {
        var document = LrcParser.Parse("[01:20.00][02:15.50]重复的副歌");

        Equal("多时间戳展开成多行", 2, document.Lines.Count);
        Equal("第一份", TimeSpan.FromSeconds(80), document.Lines[0].Start);
        Equal("第二份", TimeSpan.FromSeconds(135.5), document.Lines[1].Start);
        True("两份正文相同", document.Lines[0].Text == "重复的副歌" && document.Lines[1].Text == "重复的副歌");
    }

    private static void CheckUntimedAndBlankLines()
    {
        var document = LrcParser.Parse(
            """
            [00:01.00]有时间的

            没有时间戳的行
            [00:03.00]
            """);

        Equal("空行不产生歌词行", 3, document.Lines.Count);
        Equal("已打轴", 2, document.TimedLines.Count);
        Equal("未打轴", 1, document.UntimedLines.Count);
        Equal("未打轴的正文", "没有时间戳的行", document.UntimedLines[0].Text);

        // 只有时间戳、没有正文：文件确实声明了这个时刻，就该画出这条线。
        Equal("只有时间戳也保留", string.Empty, document.TimedLines[1].Text);
        Equal("未打轴的行没有起点", true, document.UntimedLines[0].Start is null);
    }

    private static void CheckOrderAndDuplicates()
    {
        var document = LrcParser.Parse(
            """
            [00:30.00]晚的
            [00:10.00]早的
            [00:10.00]同刻的另一行
            """);

        // 文件原序不动：打轴编辑时「行的顺序」就是工作顺序，解析时重排会把工作顺序打乱。
        Equal("原序第 1 行", "晚的", document.Lines[0].Text);

        // 排序只发生在派生视图里，而且必须稳定——同一时刻的两行保持文件中的先后。
        Equal("派生视图按时间升序", "早的", document.TimedLines[0].Text);
        Equal("同刻保持文件原序", "同刻的另一行", document.TimedLines[1].Text);
        Equal("同刻不去重", 3, document.TimedLines.Count);
        Equal("派生视图不改动原序", "晚的", document.Lines[0].Text);
    }

    private static void CheckBracketedText()
    {
        var document = LrcParser.Parse(
            """
            [00:05.00][笑]这句的方括号是正文
            [合唱]整行都没有时间戳
            """);

        Equal("行数", 2, document.Lines.Count);

        // 「时间戳 + 方括号批注」是真实存在的写法，不能因为多了一个方括号就丢掉合法时间戳。
        Equal("时间戳照常生效", TimeSpan.FromSeconds(5), document.TimedLines[0].Start);
        Equal("方括号起算作正文", "[笑]这句的方括号是正文", document.TimedLines[0].Text);

        // 整行都没有时间戳时，方括号同样原样保留。
        Equal("整行为未打轴", 1, document.UntimedLines.Count);
        Equal("方括号原样保留", "[合唱]整行都没有时间戳", document.UntimedLines[0].Text);
    }

    private static void CheckMalformed()
    {
        var document = LrcParser.Parse(
            """
            [00:08.00
            [+1:-2]不是时间戳
            []空方括号
            """);

        // 三种都不是时间戳，因此都是未打轴的正文，一个字都不丢。
        Equal("全部当作未打轴的正文", 3, document.Lines.Count);
        Equal("未闭合的方括号", "[00:08.00", document.Lines[0].Text);
        Equal("带正负号的不算时间戳", "[+1:-2]不是时间戳", document.Lines[1].Text);
        Equal("空方括号", "[]空方括号", document.Lines[2].Text);

        // 秒位超过 59 照收不进位纠错：文件写什么就读成什么，交给用户判断。
        var loose = LrcParser.Parse("[99:99.999]秒位越界");
        Equal("秒位越界仍然解析", TimeSpan.FromSeconds(99 * 60 + 99.999), loose.TimedLines[0].Start);
    }

    private static void CheckEncoding()
    {
        var utf8 = Encoding.UTF8.GetBytes("[00:01.00]中文");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("[00:01.00]中文"))
            .ToArray();

        // B2 E2 CA D4 是「测试」的 GBK 编码，不是合法的 UTF-8。
        byte[] gbk =
        [
            (byte)'[', (byte)'0', (byte)'0', (byte)':', (byte)'0', (byte)'1', (byte)'.', (byte)'0', (byte)'0', (byte)']',
            0xB2, 0xE2, 0xCA, 0xD4,
        ];

        var fromUtf8 = LrcParser.DecodeText(utf8, out var utf8Warning);
        var fromBom = LrcParser.DecodeText(withBom, out _);
        var fromUtf16 = LrcParser.DecodeText(utf16, out _);
        var fromGbk = LrcParser.DecodeText(gbk, out var gbkWarning);

        Equal("UTF-8 无 BOM", "[00:01.00]中文", fromUtf8);
        True("UTF-8 无 BOM 不报警", utf8Warning is null);
        Equal("UTF-8 带 BOM 去掉 BOM", "[00:01.00]中文", fromBom);
        Equal("UTF-16 LE 带 BOM", "[00:01.00]中文", fromUtf16);
        True("GBK 给出警告", gbkWarning is not null);

        // 时间戳是 ASCII，换哪种编码都是对的；坏掉的只有正文。这正是这个回退存在的理由：
        // 报错拒绝导入会让用户以为文件坏了，而实际上他要的信息（时间轴）一分不少。
        Equal("GBK 文件的时间戳仍然正确", TimeSpan.FromSeconds(1), LrcParser.Parse(fromGbk).TimedLines[0].Start);
    }

    // ------------------------------------------------------------------ 断言

    private static void Equal<T>(string what, T expected, T actual) => Checks.Equal(what, expected, actual);

    private static void True(string what, bool condition) => Checks.True(what, condition);
}
