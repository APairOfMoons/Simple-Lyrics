using SimpleLyrics.Core.Editing;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Probe;

/// <summary>
/// LRC 的时间码、写出与时间轴约束，对应 PLAN.md §9 与 §7.6。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="LyricsProbe"/> 分开：那一份测的是「读进来的东西对不对」，
/// 这一份测的是「写出去的东西对不对」以及「改动落在数据上是什么效果」。
/// 两者都是纯逻辑，没有 UI 依赖，将来 <c>tests/SimpleLyrics.Core.Tests</c> 立项后
/// 可以整份平移过去。
/// </para>
/// <para>
/// 这里刻意覆盖一处容易被忽略的耦合：<b>写文件时按时间升序，读回来时保持文件原序</b>。
/// 若有人把写出改成照 <c>Lines</c> 的原序写，往返断言不会失败（读回来还是原序），
/// 但拖动之后导出文件里就会出现乱序的时间戳——所以另有一条直接检查输出文本顺序的断言。
/// </para>
/// </remarks>
internal static class LyricEditProbe
{
    public static void Run()
    {
        Checks.Reset();

        Console.WriteLine("---- LRC 时间码 / 写出 / 时间轴约束 ----");

        CheckTimecode();
        CheckWriterRoundTrip();
        CheckWriterOrderAndMetadata();
        CheckSuggestPath();
        CheckResolve();
        CheckUndoAndRedo();

        Console.WriteLine($"  合计 {Checks.Passed} 通过, {Checks.Failed} 失败");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 时间码

    private static void CheckTimecode()
    {
        // 两位小数按十进制读，与解析端 .5 = 500 毫秒是同一条规则的两头。
        Checks.Equal("整秒", "01:30.00", LrcTimecode.Format(TimeSpan.FromSeconds(90)));
        Checks.Equal("厘秒", "00:12.34", LrcTimecode.Format(TimeSpan.FromSeconds(12.34)));
        Checks.Equal("毫秒按四舍五入取整", "00:12.35", LrcTimecode.Format(TimeSpan.FromSeconds(12.3456)));
        Checks.Equal("满一小时带小时位", "1:02:03.46", LrcTimecode.Format(TimeSpan.FromSeconds(3723.456)));
        Checks.Equal("负时刻带负号", "-00:01.50", LrcTimecode.Format(TimeSpan.FromSeconds(-1.5)));

        // 取整必须在「格」上做，而不是在秒上乘 100 再取整：
        // 0.02 在二进制下是 0.0200000000000000004，走 double 会向上多进一格。
        Checks.Equal("一格的边界不向上跑", TimeSpan.FromSeconds(0.02), LrcTimecode.Quantize(TimeSpan.FromSeconds(0.02)));
        Checks.Equal("2 格 = 20 毫秒", 2L, LrcTimecode.ToUnits(TimeSpan.FromSeconds(0.02)));
        Checks.Equal("3 格 = 30 毫秒", 3L, LrcTimecode.ToUnits(TimeSpan.FromSeconds(0.026)));
        Checks.Equal("格与时刻互转", TimeSpan.FromSeconds(12.34), LrcTimecode.FromUnits(LrcTimecode.ToUnits(TimeSpan.FromSeconds(12.34))));

        // 零点五格（5 毫秒）必须向远离零的方向进位，否则「拖到刚好一半」会静默地少 10 毫秒。
        Checks.Equal("半格向上进位", TimeSpan.FromSeconds(0.01), LrcTimecode.Quantize(TimeSpan.FromSeconds(0.005)));
    }

    // ------------------------------------------------------------------ 写出

    private static void CheckWriterRoundTrip()
    {
        const string Source =
            """
            [ti:夜色]
            [ar:某人]
            [al:某专辑]
            [by:制作者]
            [offset:+250]
            [length:03:54]
            [00:12.34]第一行
            [01:20.00][02:15.50]重复的副歌
            [00:30.00]
            没有时间戳的一行
            [00:07.00][笑]方括号是正文
            """;

        var original = LrcParser.Parse(Source);
        var text = LrcWriter.Format(original);
        var reloaded = LrcParser.Parse(text);
        var again = LrcWriter.Format(reloaded);

        // 幂等：写出去、读回来、再写出去，必须逐字节相同。
        Checks.Equal("写出是幂等的", text, again);
        Checks.Equal("行数不变", original.Lines.Count, reloaded.Lines.Count);
        Checks.Equal("已打轴行数不变", original.TimedLines.Count, reloaded.TimedLines.Count);
        Checks.Equal("未打轴行数不变", original.UntimedLines.Count, reloaded.UntimedLines.Count);
        Checks.Equal("标题往返", "夜色", reloaded.Metadata.Title);
        Checks.Equal("艺术家往返", "某人", reloaded.Metadata.Artist);
        Checks.Equal("专辑往返", "某专辑", reloaded.Metadata.Album);
        Checks.Equal("制作者往返", "制作者", reloaded.Metadata.Author);
        Checks.Equal("offset 往返", 250, reloaded.Metadata.OffsetMilliseconds);
        Checks.Equal("不认识的标签往返", "03:54", reloaded.Metadata.Extra.GetValueOrDefault("length"));

        var timedBefore = original.TimedLines;
        var timedAfter = reloaded.TimedLines;

        var same = timedBefore.Count == timedAfter.Count;

        for (var index = 0; same && index < timedBefore.Count; index++)
        {
            same = timedBefore[index].Start == timedAfter[index].Start
                && timedBefore[index].Text == timedAfter[index].Text;
        }

        Checks.True("每一行的时刻与正文都往返一致", same);

        // 正文自带的方括号要原样写回，不能被当成标签吃掉。
        Checks.True(
            "正文里的方括号往返",
            reloaded.Lines.Exists(static line => line.Text == "[笑]方括号是正文" && line.Start == TimeSpan.FromSeconds(7)));

        // 只有时间戳、没有正文的那一行同样要留住：它标的是一个真实存在的位置。
        Checks.True(
            "空正文的时间戳往返",
            reloaded.Lines.Exists(static line => line.Text.Length == 0 && line.Start == TimeSpan.FromSeconds(30)));

        // 正文里带换行会写出一个结构上坏掉的文件（第二行会变成一条没有时间戳的正文），
        // 所以压成空格：压平之后仍是<b>一行</b>，时间戳还在。
        var awkward = new LyricDocument();
        awkward.Lines.Add(new LyricLine { Text = "上句\n下句", Start = TimeSpan.FromSeconds(1) });

        var flattened = LrcParser.Parse(LrcWriter.Format(awkward));

        Checks.Equal("正文里的换行被压成空格后仍是一行", 1, flattened.Lines.Count);
        Checks.Equal("压平后的正文", "上句 下句", flattened.TimedLines[0].Text);
        Checks.Equal("压平之后时间戳还在", TimeSpan.FromSeconds(1), flattened.TimedLines[0].Start);
    }

    private static void CheckWriterOrderAndMetadata()
    {
        var document = new LyricDocument();

        // 文件原序是「晚的」在前——拖过之后这正是常见状态。
        document.Lines.Add(new LyricLine { Text = "晚的", Start = TimeSpan.FromSeconds(30) });
        document.Lines.Add(new LyricLine { Text = "早的", Start = TimeSpan.FromSeconds(10) });
        document.Lines.Add(new LyricLine { Text = "没打轴的" });
        document.Metadata.Title = "标题";
        document.Metadata.Extra["length"] = "03:54";
        document.Metadata.Extra["alpha"] = "x";

        var lines = LrcWriter.Format(document).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Checks.Equal("头部标签行", "[ti:标题]", lines[0]);
        Checks.Equal("不认识的标签按键名排序", "[alpha:x]", lines[1]);
        Checks.Equal("不认识的标签按键名排序（二）", "[length:03:54]", lines[2]);

        // 已打轴的按时间升序，未打轴的排在最后。
        Checks.Equal("最早的一行排在第一", "[00:10.00]早的", lines[3]);
        Checks.Equal("晚的一行排在后面", "[00:30.00]晚的", lines[4]);
        Checks.Equal("未打轴的行没有时间戳", "没打轴的", lines[5]);

        // 写出不改动文档：Lines 仍是文件原序，那是界面的工作顺序。
        Checks.Equal("写出不动原序", "晚的", document.Lines[0].Text);

        // 零偏移不写：它是默认值，写一行只会让文件多一句废话。
        Checks.True("零 offset 不写出", !LrcWriter.Format(new LyricDocument()).Contains("offset"));
    }

    private static void CheckSuggestPath()
    {
        // 还没有来源文件时的建议保存位置：与音频同名同目录。这就是「从零创建歌词」那条路。
        Checks.Equal(
            "建议路径",
            Path.Combine("E:\\samples", "test.lrc"),
            LrcWriter.SuggestPath("E:\\samples\\test.flac"));

        Checks.Equal("没有音频时给不出建议", null, LrcWriter.SuggestPath(null));
        Checks.Equal("空路径给不出建议", null, LrcWriter.SuggestPath(string.Empty));
    }

    // ------------------------------------------------------------------ 最小间隔

    private static void CheckResolve()
    {
        // 两条歌词挨得太近：被拖过来的那一条让位，另一条一动不动。
        var stationary = new LyricLine { Text = "站着的", Start = TimeSpan.FromSeconds(10) };
        var moving = new LyricLine { Text = "被拖来的", Start = TimeSpan.FromSeconds(50) };

        var all = new List<LyricLine> { stationary, moving };
        var targets = new List<LyricTimeTarget> { new(moving, TimeSpan.FromSeconds(10.01)) };

        var resolved = LyricTimeline.Resolve(targets, all, TimeSpan.FromSeconds(0.02));

        Checks.NearTime("挤在一起时错开到 20 毫秒之外", TimeSpan.FromSeconds(10.02), resolved[0]);
        Checks.Equal("未被拖动的行不动", TimeSpan.FromSeconds(10), stationary.Start);

        // 落在别人头上时，按这一行原本的先后决定往哪边让：本来在后，就往晚让。
        var ahead = new LyricLine { Text = "本来在前", Start = TimeSpan.FromSeconds(1) };
        var behind = new LyricLine { Text = "本来在后", Start = TimeSpan.FromSeconds(9) };

        var pair = new List<LyricLine> { ahead, behind };

        var late = LyricTimeline.Resolve(
            [new LyricTimeTarget(behind, TimeSpan.FromSeconds(1))],
            pair,
            TimeSpan.FromSeconds(0.02));

        Checks.NearTime("本来在后的落在别人头上时往后让", TimeSpan.FromSeconds(1.02), late[0]);

        var early = LyricTimeline.Resolve(
            [new LyricTimeTarget(ahead, TimeSpan.FromSeconds(9))],
            pair,
            TimeSpan.FromSeconds(0.02));

        Checks.NearTime("本来在前的落在别人头上时往前让", TimeSpan.FromSeconds(8.98), early[0]);

        // 音频之前没有内容，负时刻连 LRC 都写不出来，所以 0 秒是硬下界。
        var first = new LyricLine { Text = "开头", Start = TimeSpan.FromSeconds(30) };

        var clamped = LyricTimeline.Resolve(
            [new LyricTimeTarget(first, TimeSpan.FromSeconds(-12))],
            [first],
            TimeSpan.FromSeconds(0.02));

        Checks.Equal("拖到音频之前停在 0 秒", TimeSpan.Zero, clamped[0]);

        // 往上顶到 0 秒附近时也只能往晚让——往早让会落进音频之前。
        var anchor = new LyricLine { Text = "第零秒", Start = TimeSpan.Zero };
        var pushed = new LyricLine { Text = "顶上来的", Start = TimeSpan.FromSeconds(20) };

        var atZero = LyricTimeline.Resolve(
            [new LyricTimeTarget(pushed, TimeSpan.Zero)],
            [anchor, pushed],
            TimeSpan.FromSeconds(0.02));

        Checks.NearTime("0 秒处只能往晚让", TimeSpan.FromSeconds(0.02), atZero[0]);

        // 一整组一起拖：组内的相对间距原样保留（都躲开了别人，谁也不撞谁）。
        var group = new List<LyricLine>
        {
            new() { Text = "A", Start = TimeSpan.FromSeconds(100) },
            new() { Text = "B", Start = TimeSpan.FromSeconds(101) },
            new() { Text = "C", Start = TimeSpan.FromSeconds(102) },
            new() { Text = "挡路的", Start = TimeSpan.FromSeconds(150) },
        };

        var shift = new List<LyricTimeTarget>
        {
            new(group[0], TimeSpan.FromSeconds(150)),
            new(group[1], TimeSpan.FromSeconds(151)),
            new(group[2], TimeSpan.FromSeconds(152)),
        };

        var plan = LyricTimeline.Plan(shift, group, TimeSpan.FromSeconds(0.02));

        Checks.Equal("一组都落到位", 3, plan.Report.Changed);

        // A 顶在「挡路的」上，必须错开；B、C 的位置本来就没挨着别人。
        Checks.Equal("顶上去的那条被错开", 1, plan.Report.Nudged);
        Checks.True("错开幅度就是最小间隔", Math.Abs(plan.Report.LargestNudge.TotalMilliseconds - 20) < 0.001);

        // 算计划时一行都不许动：改动要经由一步可撤销的命令落下去。
        Checks.Equal("算计划时不动数据", TimeSpan.FromSeconds(100), group[0].Start);

        var command = new TimeShiftCommand(plan.Changes);
        command.Apply();

        Checks.NearTime("落下去之后 A 被错开", TimeSpan.FromSeconds(149.98), group[0].Start);
        Checks.Equal("组内其余两条不受影响", TimeSpan.FromSeconds(151), group[1].Start);
        Checks.Equal("组内其余两条不受影响（二）", TimeSpan.FromSeconds(152), group[2].Start);

        // 错开之后每两条之间都必须够远——这才是这条约束真正要保证的事。
        Checks.True($"错开之后最紧的一对是 {Tightest(group):F0} 毫秒（须 ≥ 20）", Tightest(group) >= 20 - 1e-9);

        command.Revert();

        Checks.Equal("撤销之后回到原处", TimeSpan.FromSeconds(100), group[0].Start);
        Checks.Equal("撤销之后回到原处（二）", TimeSpan.FromSeconds(101), group[1].Start);

        // 间隔的格数由 ticks 整数算出：0.02 秒必须正好是 2 格而不是 3 格。
        Checks.Equal("20 毫秒 = 2 格", 2L, LyricTimeline.MinimumUnits(TimeSpan.FromSeconds(0.02)));
        Checks.Equal("15.963 毫秒向上取整到 2 格", 2L, LyricTimeline.MinimumUnits(TimeSpan.FromSeconds(0.015963)));
        Checks.Equal("不足一格也算一格", 1L, LyricTimeline.MinimumUnits(TimeSpan.FromSeconds(0.001)));
    }

    /// <summary>一组歌词里最紧挨着的那一对，相隔多少毫秒。</summary>
    private static double Tightest(IEnumerable<LyricLine> lines)
    {
        var ordered = new List<TimeSpan>();

        foreach (var line in lines)
        {
            if (line.Start is { } start) ordered.Add(start);
        }

        ordered.Sort();

        var tightest = double.MaxValue;
        for (var index = 1; index < ordered.Count; index++)
        {
            tightest = Math.Min(tightest, (ordered[index] - ordered[index - 1]).TotalMilliseconds);
        }

        return tightest;
    }

    // ------------------------------------------------------------------ 撤销

    /// <summary>
    /// 撤销 / 重做这套框架。
    /// </summary>
    /// <remarks>
    /// 这里是「框架」而不是「拖动」的测试：它只认 <see cref="IEditCommand"/>，
    /// 将来粘贴、盖章、批量偏移各自做成命令之后，走的是同一组断言里的同一条路径。
    /// </remarks>
    private static void CheckUndoAndRedo()
    {
        var first = new LyricLine { Text = "甲", Start = TimeSpan.FromSeconds(10) };
        var second = new LyricLine { Text = "乙", Start = TimeSpan.FromSeconds(20) };

        var all = new List<LyricLine> { first, second };
        var history = new TimelineEditHistory();

        Checks.Equal("初始没有可撤销的", null, history.Undo());
        Checks.Equal("初始没有可重做的", null, history.Redo());
        Checks.Equal("初始位置为 0", 0, history.Position);

        // 第一次：把甲往后挪。
        var firstPlan = LyricTimeline.Plan(
            [new LyricTimeTarget(first, TimeSpan.FromSeconds(30))],
            all,
            TimeSpan.FromSeconds(0.02));

        history.Push(new TimeShiftCommand(firstPlan.Changes));

        Checks.Equal("压入之后甲动了", TimeSpan.FromSeconds(30), first.Start);
        Checks.Equal("位置推进一格", 1, history.Position);
        Checks.True("可以撤销", history.CanUndo);

        // 第二次：把乙往前挪。
        var secondPlan = LyricTimeline.Plan(
            [new LyricTimeTarget(second, TimeSpan.FromSeconds(40))],
            all,
            TimeSpan.FromSeconds(0.02));

        history.Push(new TimeShiftCommand(secondPlan.Changes));

        Checks.Equal("第二条改动也落下去", TimeSpan.FromSeconds(40), second.Start);
        Checks.Equal("位置推进两格", 2, history.Position);

        Checks.Equal("撤销第二次", "调整 1 行歌词的时间码", history.Undo());
        Checks.Equal("甲不受影响", TimeSpan.FromSeconds(30), first.Start);
        Checks.Equal("乙回到原处", TimeSpan.FromSeconds(20), second.Start);
        Checks.True("现在可以重做", history.CanRedo);

        Checks.Equal("重做第二次", "调整 1 行歌词的时间码", history.Redo());
        Checks.Equal("乙又到了新位置", TimeSpan.FromSeconds(40), second.Start);

        history.Undo();
        history.Undo();

        Checks.Equal("撤到底之后再撤没有东西", null, history.Undo());
        Checks.Equal("两条都回到原处", TimeSpan.FromSeconds(10), first.Start);
        Checks.Equal("两条都回到原处（二）", TimeSpan.FromSeconds(20), second.Start);
        Checks.Equal("位置回到 0", 0, history.Position);
        Checks.True("撤到底之后不能撤销", !history.CanUndo);
        Checks.Equal("撤到底之后还能重做两步", 2, history.RedoCount);

        // 撤到底之后做一件新事：原来的重做支路必须作废，否则「重做」会跳到一个
        // 与当前状态接不上的过去。
        history.Redo();
        history.Push(new TimeShiftCommand(
            LyricTimeline.Plan(
                [new LyricTimeTarget(second, TimeSpan.FromSeconds(99))],
                all,
                TimeSpan.FromSeconds(0.02)).Changes));

        Checks.Equal("新改动清空重做栈", 0, history.RedoCount);
        Checks.Equal("新改动落在当前位置之后", 2, history.Position);

        // 没有真的挪动的拖动不该占用一步撤销：否则按一次 Ctrl+Z 会看起来毫无反应。
        var idle = LyricTimeline.Plan(
            [new LyricTimeTarget(second, TimeSpan.FromSeconds(99))],
            all,
            TimeSpan.FromSeconds(0.02));

        Checks.Equal("落在原处时清单是空的", 0, idle.Changes.Count);
        Checks.Equal("落在原处时也不报改动", 0, idle.Report.Changed);

        // 换了一份歌词之后历史必须整体丢掉——旧命令里握着的是上一份文档的行对象。
        history.Clear();

        Checks.Equal("清空之后没有可撤销的", 0, history.UndoCount);
        Checks.Equal("清空之后没有可重做的", 0, history.RedoCount);

        // 撤销是精确还原，不是重算：错开带来的位移也一并回去。
        var tight = new LyricLine { Text = "站着", Start = TimeSpan.FromSeconds(5) };
        var moved = new LyricLine { Text = "拖过来", Start = TimeSpan.FromSeconds(60) };

        var pair = new List<LyricLine> { tight, moved };
        var nudgedHistory = new TimelineEditHistory();

        nudgedHistory.Push(new TimeShiftCommand(
            LyricTimeline.Plan(
                [new LyricTimeTarget(moved, TimeSpan.FromSeconds(5))],
                pair,
                TimeSpan.FromSeconds(0.02)).Changes));

        Checks.NearTime("错开之后落在 5.02 秒", TimeSpan.FromSeconds(5.02), moved.Start);

        nudgedHistory.Undo();

        Checks.Equal("撤销把错开一起还原", TimeSpan.FromSeconds(60), moved.Start);
        Checks.Equal("未被拖动的行始终没动", TimeSpan.FromSeconds(5), tight.Start);
    }
}
