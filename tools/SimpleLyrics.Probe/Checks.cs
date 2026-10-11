namespace SimpleLyrics.Probe;

/// <summary>
/// 探针里那几条断言，以及它们的计数。
/// </summary>
/// <remarks>
/// 测试项目尚未获准引入测试框架（见 PLAN §6），所以断言先手写在探针里。
/// 单独摆一个文件是为了让各组的结论能合成一个总数——不然每一组各报各的，
/// 跑完还得人去加。
/// </remarks>
internal static class Checks
{
    public static int Passed { get; private set; }

    public static int Failed { get; private set; }

    public static void Reset()
    {
        Passed = 0;
        Failed = 0;
    }

    public static void Equal<T>(string what, T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Passed++;
            return;
        }

        Failed++;
        Console.WriteLine($"  × {what}：期望 {Describe(expected)}，实际 {Describe(actual)}");
    }

    public static void True(string what, bool condition)
    {
        if (condition)
        {
            Passed++;
            return;
        }

        Failed++;
        Console.WriteLine($"  × {what}");
    }

    /// <summary>
    /// 比时刻，允许 1 tick 的差。
    /// </summary>
    /// <remarks>
    /// <c>TimeSpan.FromSeconds(5.02)</c> 的值是 50199999 或 50200000，取决于那个十进制小数
    /// 在二进制下落到哪一边——而时间码算出的是整整 50200000。用严格相等去比，
    /// 断言会时不时因为一个 tick 而变红，而那个红说明不了任何事。
    /// </remarks>
    public static void NearTime(string what, TimeSpan expected, TimeSpan? actual)
    {
        // null 表示那一行未打轴，不该被当成「差了 0 个 tick」。
        if (actual is { } value && Math.Abs((expected - value).Ticks) <= 1)
        {
            Passed++;
            return;
        }

        Failed++;
        Console.WriteLine($"  × {what}：期望 {Describe(expected)}，实际 {Describe(actual)}");
    }

    /// <summary>
    /// 断言失败时的时间显示，<b>带上天与小时的位</b>。
    /// </summary>
    /// <remarks>
    /// 这里踩过一次：原本用 <c>mm\:ss\.fff</c>，它只输出「分钟分量」，超过一小时的量
    /// 会被悄悄丢掉。当时正是一行 <c>new TimeSpan(1, 2, 3, 450)</c> 的断言失败——
    /// 那个重载其实是（天, 时, 分, 秒），值等于 26 小时 10 分 30 秒，
    /// 显示成 <c>10:30.000</c> 之后看上去完全像是解析器把 <c>[1:02:03.45]</c> 读错了。
    /// 诊断信息把错误指向了错误的地方，比没有诊断信息更费时间。
    /// </remarks>
    public static string Describe<T>(T value) => value switch
    {
        null => "null",
        TimeSpan time => time.ToString(@"d\.hh\:mm\:ss\.fff"),
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "null",
    };
}
