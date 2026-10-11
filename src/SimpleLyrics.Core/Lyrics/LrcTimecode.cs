using System.Globalization;

namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// LRC 时间码的格式与粒度——全项目唯一一份。
/// </summary>
/// <remarks>
/// <para>
/// <b>LRC 只存得到厘秒（10 毫秒）。</b>它是 <c>mm:ss.ff</c>，小数部分是两位十进制数字，
/// 而两位小数按十进制读就是百分之一秒——与解析端（<see cref="LrcParser"/> 把 <c>.5</c>
/// 读成 500 毫秒）是同一条规则的两头。格式与粒度集中在这里，于是写文件、把拖拽的结果
/// 对齐到可表示的刻度、以及诊断工具里的断言，用的是同一份实现。
/// </para>
/// <para>
/// <b>为什么取整发生在拖拽的那一刻，而不是在保存时。</b>若只在落盘时取整，
/// 「屏幕上量到的位置」「界面上显示的时间戳」「文件里写下的数字」就成了三个不同的值，
/// 而用户唯一的对照手段正是它们三者。拖拽时即取整之后，显示的就是要写下去的，
/// 既不存在「保存完歌词跳了一下」，也不必让显示端再单独取整一次。
/// </para>
/// <para>
/// 取整用整数刻度换算，而不是 <c>Math.Round(seconds * 100)</c>：后者在
/// <c>0.02</c> 这类二进制下不精确的十进制小数上会给出比预期大一格的结果。
/// </para>
/// </remarks>
public static class LrcTimecode
{
    /// <summary>一秒钟里有多少格时间码。小数部分是两位十进制数字，所以是 100。</summary>
    public const int UnitsPerSecond = 100;

    /// <summary>一格时间码的 ticks（10 毫秒 = 100000 ticks，除得尽）。</summary>
    private const long TicksPerUnit = TimeSpan.TicksPerSecond / UnitsPerSecond;

    /// <summary>一格的时长（10 毫秒）。</summary>
    public static readonly TimeSpan Resolution = TimeSpan.FromTicks(TicksPerUnit);

    /// <summary>把时刻换算成「第几格」，向最近的格取整。</summary>
    public static long ToUnits(TimeSpan value) =>
        (long)Math.Round(value.Ticks / (double)TicksPerUnit, MidpointRounding.AwayFromZero);

    /// <summary>把「第几格」换算回时刻。</summary>
    public static TimeSpan FromUnits(long units) => TimeSpan.FromTicks(units * TicksPerUnit);

    /// <summary>取整到最近的一格。</summary>
    public static TimeSpan Quantize(TimeSpan value) => FromUnits(ToUnits(value));

    /// <summary>
    /// 格式化成 <c>mm:ss.ff</c>；满一小时带小时位，负时刻带负号。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 逾一小时写成 <c>h:mm:ss.ff</c> 而不是把分钟累加下去（<c>100:12.34</c>）：
    /// 解析端两种写法都收，但写出来时统一成带小时位更好读，也不会让分钟位变成一个
    /// 越来越长的数字。往返因此不是逐字相同，而是<b>时间值相同</b>——这是格式的规范
    /// 化，不是信息丢失。
    /// </para>
    /// <para>
    /// 用不变文化格式化：文件格式不该随系统区域设置换成非 ASCII 数字。
    /// </para>
    /// </remarks>
    public static string Format(TimeSpan value)
    {
        var units = ToUnits(value);
        var negative = units < 0;

        // 取绝对值放在「格数」上而不是 ticks 上：格的数量级只有 ticks 的十万分之一，
        // 永远不会碰到 long 的边界（TimeSpan.MinValue 的 ticks 求反才会溢出）。
        var magnitude = negative ? -units : units;

        var fraction = magnitude % UnitsPerSecond;
        magnitude /= UnitsPerSecond;

        var seconds = magnitude % 60;
        magnitude /= 60;

        var minutes = magnitude % 60;
        var hours = magnitude / 60;

        var text = hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}.{fraction:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{fraction:00}");

        return negative ? "-" + text : text;
    }
}
