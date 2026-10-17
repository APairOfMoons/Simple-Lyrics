using SimpleLyrics.Core.Editing;

namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 一次盖章——把「未绑定」栏栈顶那一行绑到某个时刻——的结果。
/// </summary>
/// <param name="Line">刚被绑到时间轴上的那一行。</param>
/// <param name="Wanted">用户盖下去的那个时刻（已量化到 LRC 时间码）。</param>
/// <param name="Final">它实际落在的时刻；与 <paramref name="Wanted"/> 不同就说明被错开了。</param>
/// <param name="Remaining">盖完之后「未绑定」栏里还剩几行。</param>
/// <remarks>
/// 报出「本来要盖在哪儿」与「实际落在哪儿」两个值，是为了让状态栏能把<b>错开</b>这件事
/// 说出来：用户瞄着指示线按下去，那一条却落在了别处——不说的话看上去就像打轴打偏了，
/// 而真正的原因（离上一条太近）在屏幕上恰恰是看不见的。
/// </remarks>
public readonly record struct LyricStampReport(
    LyricLine Line, TimeSpan Wanted, TimeSpan Final, int Remaining)
{
    /// <summary>这一行因为离相邻歌词太近而被错开了。</summary>
    public bool Nudged => Final != Wanted;

    /// <summary>错开的幅度（往后为正）。</summary>
    public TimeSpan Nudge => Final - Wanted;
}

/// <summary>
/// 当前这一份歌词工作：一份文档、它从哪儿来、撤销栈、以及保存点与去处。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这一层。</b>在这之前，「文档」「撤销栈」「保存到哪儿」「脏不脏」四样东西
/// 散在视图模型里，于是每加一条载入路径都要在四个地方各记得改一次——而漏掉任何一处都不报错，
/// 只会在某个少见的顺序下表现成「明明保存过了却还标着未保存」或者反过来的静默覆盖。
/// 收进一个不依赖 UI 的类之后，这些规则全都成了纯逻辑，可以在探针里断言。
/// </para>
/// <para>
/// <b>文档实例从生到死只有一个。</b>载入是「把内容搬进来」而不是「换一个对象」。
/// 于是界面层不必关心「文档换了没有」——它只管在改动之后重新发布那几个派生列表，
/// 而控件里那些按引用认行的选区（见 <c>LyricTrackView.PruneSelection</c>）也有一条稳定的基准。
/// </para>
/// <para>
/// <b>载入一律是整份替换。</b>导入一份 <c>.lrc</c>、换一首音频、随音频自动带上侧车，
/// 三者的共同点是「从今往后用这一份」：先把现有的全部清掉，再灌进新的。
/// 于是不存在「两套歌词混在一起」的中间状态，也就不必去猜「哪一行对应哪一行」。
/// </para>
/// <para>
/// <b>「脏不脏」看的是会写出去的内容，不是撤销栈动过没有。</b>这个判据是从需求里逼出来的：
/// 粘贴进来的行根本不进 <c>.lrc</c>（那里只写打了轴的行），所以粘贴之后再拿「撤销栈动过」
/// 去判，就会把用户挡在「换音频」的门外，而他能做的只有把自己刚粘的东西丢掉。
/// 反过来，保存之后若撤销回保存前的样子，内容一样了、自然也就不脏——这一点按撤销栈的位置
/// 记是做不到的，而漏判的表现是换音频时静默丢掉改动。
/// </para>
/// </remarks>
public sealed class LyricSession
{
    private readonly LyricDocument _document = new();

    /// <summary>上次保存（或刚载入）时那份「会写出去的内容」。</summary>
    private string _savedText = string.Empty;

    /// <summary>当前这一份歌词。实例固定，内容会变。</summary>
    public LyricDocument Document => _document;

    /// <summary>改动历史。撤销 / 重做只认它。</summary>
    public TimelineEditHistory History { get; } = new();

    /// <summary>这份歌词从哪儿来。</summary>
    public LyricOrigin Origin { get; private set; } = LyricOrigin.None;

    /// <summary>来源文件（侧车或手动导入的那个）；<c>null</c> 表示它不是从文件来的。</summary>
    public string? OriginPath { get; private set; }

    /// <summary>
    /// 保存的去处（「主去处」）。
    /// </summary>
    /// <remarks>
    /// <c>null</c> 表示还没有存过——第一次 <c>Ctrl+S</c> 要问一次保存位置。
    /// 它<b>只由保存成功来改</b>：另存为会把它换成新路径，而将来「写进音频内嵌标签」
    /// 那类一次性动作不会碰它——用户接着按 <c>Ctrl+S</c>，存的仍然该是那个 <c>.lrc</c>。
    /// </remarks>
    public ILyricSink? Sink { get; private set; }

    /// <summary>这份歌词里有没有行。</summary>
    public bool HasLyrics => _document.Lines.Count > 0;

    /// <summary>
    /// 有没有「会写进文件的内容」变了样。
    /// </summary>
    /// <remarks>
    /// 判据是把要写出去的文本与上次保存的那份比一比。写一次只有几百行的字符串拼接，
    /// 而它只在改动落定、保存、以及刷新界面文案时各算一遍——不是每帧。
    /// </remarks>
    public bool HasUnsavedChanges => LrcWriter.Format(_document) != _savedText;

    /// <summary>已打轴的行，按时间升序。</summary>
    public IReadOnlyList<LyricLine> TimedLines => _document.TimedLines;

    /// <summary>还没有打轴的行，保持文档原序。</summary>
    public IReadOnlyList<LyricLine> UntimedLines => _document.UntimedLines;

    // ------------------------------------------------------------ 载入

    /// <summary>
    /// 把一份读好的歌词整份装进来。
    /// </summary>
    /// <remarks>
    /// 撤销栈必须清掉：旧命令握着的是上一份歌词的行对象，而那些行马上就不在文档里了，
    /// 撤回去只会把一批幽灵数据塞回来。
    /// </remarks>
    /// <param name="source">读出来的那一份。</param>
    /// <param name="origin">它从哪儿来。</param>
    /// <param name="path">来源文件；粘贴时给 <c>null</c>。</param>
    /// <param name="sink">
    /// 保存的主去处。侧车与手动导入的文件都指向自己那个文件，于是第一次 <c>Ctrl+S</c>
    /// 就是原地写回；粘贴时给 <c>null</c>，第一次保存会问一次位置。
    /// </param>
    /// <returns>
    /// 被顶掉的旧行数。载入是整份替换，而旧的那一份里可能有用户自己粘进来、还没打轴的文本——
    /// 那是找不回来的东西，所以调用方拿这个数字去状态栏说一声。
    /// </returns>
    public int LoadFrom(LyricDocument source, LyricOrigin origin, string? path, ILyricSink? sink)
    {
        var replaced = _document.Lines.Count;

        History.Clear();
        _document.Adopt(source);

        Origin = origin;
        OriginPath = path;
        Sink = sink;

        // 刚载入的东西当然不算「有未保存的改动」。
        _savedText = LrcWriter.Format(_document);

        return replaced;
    }

    /// <summary>
    /// 把一段粘贴进来的纯文本追加到未绑定那一侧。
    /// </summary>
    /// <remarks>
    /// 它<b>只加行、不碰时间轴</b>，而且这些行不进 <c>.lrc</c>——正因为如此，
    /// 这一步并不让会话变「脏」（见 <see cref="HasUnsavedChanges"/>）。
    /// </remarks>
    /// <returns>加进来的行数；文本里没有可用的行时是 0。</returns>
    public int ImportText(string text)
    {
        var texts = PlainTextLyrics.Split(text);
        if (texts.Count == 0) return 0;

        var lines = new List<LyricLine>(texts.Count);
        foreach (var line in texts) lines.Add(new LyricLine { Text = line });

        Apply(new AddLyricLinesCommand(_document, lines));

        // 从零开始的这份歌词，出身就是「粘贴」——在此之前它什么都不算。
        if (Origin == LyricOrigin.None) Origin = LyricOrigin.Pasted;

        return lines.Count;
    }

    /// <summary>
    /// 清空整份歌词（换音频、以及「清空歌词」那个动作）。
    /// </summary>
    /// <remarks>
    /// 连来源、去处、保存点一起清掉：这些说的都是「这份歌词」的事，而这份歌词马上就没有了。
    /// 撤销栈同样清——它记的改动全都发生在那份已经不存在的歌词上。
    /// </remarks>
    /// <returns>被清掉的行数，供状态栏说明。</returns>
    public int Clear()
    {
        var cleared = _document.Lines.Count;

        History.Clear();
        _document.Clear();

        Origin = LyricOrigin.None;
        OriginPath = null;
        Sink = null;
        _savedText = string.Empty;

        return cleared;
    }

    // ------------------------------------------------------------ 改动

    /// <summary>
    /// 落下一步可撤销的改动。
    /// </summary>
    /// <remarks>
    /// <b>所有改动都必须走这里</b>，包括「只改一个字段」的那种。命令负责改数据、
    /// 本方法负责压进历史并作废派生缓存（<see cref="LyricDocument.Invalidate"/>）——
    /// 这两件事必须成对发生，而分开写迟早会漏掉一处，漏掉的表现是「时间改了、位置没动」
    /// 或者「撤销栈里有一步撤销不掉的改动」。
    /// </remarks>
    public void Apply(IEditCommand command)
    {
        History.Push(command);
        _document.Invalidate();
    }

    /// <summary>
    /// 撤销一步，并作废派生缓存。
    /// </summary>
    /// <remarks>
    /// 撤销与重做之所以也走会话，而不是让调用方自己去问 <see cref="History"/>：
    /// 「改了数据就要作废缓存」这条配对必须成对发生，而命令本身刻意不碰缓存
    /// （见 <see cref="IEditCommand"/>）。放在这里，产品与诊断工具就不会有一条路径漏掉——
    /// 漏掉的表现是「时间改了、位置没动」或者「撤销了但屏幕上没变」。
    /// </remarks>
    /// <returns>被撤销的那一步的说明；没有可撤销的时候是 <c>null</c>。</returns>
    public string? Undo()
    {
        var label = History.Undo();
        if (label is not null) _document.Invalidate();

        return label;
    }

    /// <summary>重做一步，并作废派生缓存。</summary>
    public string? Redo()
    {
        var label = History.Redo();
        if (label is not null) _document.Invalidate();

        return label;
    }

    /// <summary>
    /// 把「未绑定」栏栈顶那一行绑到给定时刻——打轴里的<b>盖章</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>数据上只是「<c>Start</c> 从 <c>null</c> 变成某个时刻」，所以走的是
    /// <see cref="TimeShiftCommand"/>。</b>绑定与解绑是同一件事的两个方向（§7.6），
    /// 这里不必另立一个命令类型：撤销一次就回到未绑定那一栏，而「上一次撤的是盖章
    /// 还是拖动」这个问题根本不需要回答。
    /// </para>
    /// <para>
    /// <b>错开与拖动共用同一份实现</b>（<see cref="LyricTimeline"/>）。于是「即使放到
    /// 最大放大倍率也分不开」的两条歌词，在这里就被推开到了
    /// <see cref="LyricTimeline.MinimumUnits"/> 格之外——那正是这条约束存在的理由：
    /// 挤在一起的两条在界面上再也点不中其中一条。
    /// </para>
    /// <para>
    /// <b>只认栈顶。</b>「下一个要打轴的是哪一行」由文档次序决定
    /// （<see cref="LyricDocument.UntimedLines"/> 的第一行），调用方无权指定——
    /// 界面若也能挑一行来盖，就又多出一份与那个次序平行的状态。
    /// </para>
    /// <para>
    /// <b>目标被占住时只往后让</b>（<see cref="LyricTimeTarget.OnlyLater"/>）。
    /// 这条与拖动刻意不同：被拖的行本来就有一个位置，挪到最近处最符合直觉；而盖章盖的
    /// 是顺序上接在最后的那一句，往前让会让它插进已经盖好的句子中间，时间顺序与歌词顺序
    /// 就此相反。暂停着连按几次最容易撞上这件事。
    /// </para>
    /// </remarks>
    /// <param name="at">要盖在哪个时刻，通常就是播放指示线所在的位置。</param>
    /// <param name="minimumSeparation">
    /// 两条歌词之间至少留出的时间。它由显示层的合并门槛与素材的最大放大倍率算出，
    /// 所以只有窗口那一侧知道，Core 这边只管收下。
    /// </param>
    /// <param name="compensation">
    /// <b>打点预补偿</b>：从「听到那一句」到「按下键」之间的那段反应时间，会先从
    /// <paramref name="at"/> 里减掉。
    /// <b>现在恒为 <see cref="TimeSpan.Zero"/>——功能尚未实现</b>（PLAN §8 里的
    /// <c>Shift+Enter</c> 与可配置预补偿）。它先出现在签名上，是因为「盖章的目标时刻
    /// 从哪儿算出来」这条路径上只该有这一处：将来接上时改的是调用方传什么，
    /// 打轴逻辑本身一个字都不用动。
    /// <para>
    /// 注意它与「全局延迟补偿」（<c>GlobalOffsetMs</c>）<b>不是一回事</b>：那个作用于
    /// 显示与导出、刻意不改动打点值本身，于是随时可以整体退回去；这个减掉的是一段
    /// 真实存在的操作延迟，属于打点值的一部分。
    /// </para>
    /// </param>
    /// <returns>这一次盖章的结果；「未绑定」栏里没有行时是 <c>null</c>。</returns>
    public LyricStampReport? StampTop(TimeSpan at, TimeSpan minimumSeparation, TimeSpan compensation = default)
    {
        if (_document.UntimedLines is not { Count: > 0 } waiting) return null;

        var line = waiting[0];

        // 量化在进 Plan 之前做一次：Plan 内部用的是同一个值，于是报出去的「本来要盖在哪儿」
        // 与它实际比对的完全是同一格时间码，不会被取整的边界情况分成两个说法。
        var wanted = LrcTimecode.Quantize(at - compensation);

        var plan = LyricTimeline.Plan(
            [new LyricTimeTarget(line, wanted, OnlyLater: true)],
            _document.TimedLines,
            minimumSeparation);

        // 这一行本来没有时刻，落点一定与之不同，所以清单不可能是空的。
        // 万一空了就什么都别做——落一步毫无变化的改动只会白占一次 Ctrl+Z。
        if (plan.Changes.Count == 0) return null;

        Apply(new TimeShiftCommand(plan.Changes, "把栈顶那一行绑到时间轴"));

        var final = plan.Changes[0].After ?? wanted;

        return new LyricStampReport(line, wanted, final, _document.UntimedLines.Count);
    }

    // ------------------------------------------------------------ 保存

    /// <summary>
    /// 把当前内容写到某个去处，成功之后把「保存点」挪到这里。
    /// </summary>
    /// <remarks>
    /// <b>失败时什么都不改</b>：保存点不动、主去处不动。写失败由 <see cref="ILyricSink.Write"/>
    /// 抛出来，本方法不去接——接下来那两行赋值自然就被跳过了。
    /// </remarks>
    public long Save(ILyricSink sink)
    {
        var text = LrcWriter.Format(_document);
        var bytes = sink.Write(text);

        Sink = sink;
        _savedText = text;

        return bytes;
    }
}
