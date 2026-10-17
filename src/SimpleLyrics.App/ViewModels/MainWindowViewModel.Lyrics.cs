using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleLyrics.Core.Editing;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.App.ViewModels;

/// <summary>
/// <see cref="MainWindowViewModel"/> 的歌词部分：载入、编辑、撤销与保存。
/// </summary>
/// <remarks>
/// <para>
/// 与主文件合起来才是一个类（<c>partial</c>）。那边是音频与播放，这边是歌词这一份工作。
/// </para>
/// <para>
/// <b>状态几乎全在 <see cref="LyricSession"/> 里。</b>文档、来源、撤销栈、保存点、主去处
/// 都是它的事；这里只做三件界面层才该做的事：把要显示的列表转发布出去、把事件的落点接起来、
/// 把结果说成一句人话放进状态栏。规则本身（什么时候算脏、换音频清什么、导入怎么落地）
/// 因此全是纯逻辑，能在探针里断言，不必开窗口去试。
/// </para>
/// <para>
/// <b>文档实例从生到死只有一个</b>（<see cref="LyricSession.Document"/>），载入是「把内容搬进来」。
/// 所以这里没有「歌词换了没有」这种问题，只有「改动之后重新发布」这一件事。
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel
{
    /// <summary>当前这一份歌词工作。</summary>
    private readonly LyricSession _session = new();

    /// <summary>当前歌词文档。只为少写几个字，内容与 <see cref="LyricSession.Document"/> 是同一个。</summary>
    private LyricDocument Document => _session.Document;

    // ------------------------------------------------------------ 歌词文档

    /// <summary>
    /// 已打轴的歌词行，按时间升序。绑定给波形上的歌词标识层。
    /// </summary>
    /// <remarks>
    /// 单独暴露一份，而不是让控件自己去 <see cref="LyricSession.Document"/> 里筛选：
    /// 控件每次重绘都要用它，而筛选与排序是有成本的；更重要的是「按时间升序」是
    /// 标识层分簇算法的前提，把它交给数据层保证，比让控件每次自己排一遍可靠。
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<LyricLine> _timedLyrics = [];

    /// <summary>
    /// 尚未打轴的歌词行，保持文档原序。绑定给「未绑定」栏的歌词栈。
    /// </summary>
    /// <remarks>
    /// <b>顺序就是打轴的先后</b>，所以这里不做任何排序。栈顶（第一行）是下一个要盖章的行——
    /// 那个「下一个」将来靠盖章时从列表头取，不靠界面去记。
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<LyricLine> _untimedLyrics = [];

    /// <summary>歌词栏的标题。</summary>
    [ObservableProperty]
    private string _lyricsCaption = "已绑定歌词";

    /// <summary>尚未导入歌词时歌词栏里的提示。</summary>
    [ObservableProperty]
    private string _lyricsHint =
        "尚未导入歌词\n\n点工具栏的「导入歌词…」载入 .lrc（Ctrl+I）\n"
        + "或「粘贴歌词…」贴进一段还没打轴的文本（Ctrl+T）";

    /// <summary>「未绑定」栏空着时的提示。它不随操作变化，所以是只读的。</summary>
    public string UnboundHint =>
        "还没有未绑定歌词\n\n点工具栏的「粘贴歌词…」（Ctrl+T）\n把歌词文本贴进来，一行一句";

    /// <summary>
    /// 两条歌词之间至少留出的时间；拖到更近处会被自动错开。
    /// </summary>
    /// <remarks>
    /// 由窗口那一侧从「最大放大倍率」算出来送进来（<c>LyricTrackView.MinimumSeparationFor</c>）：
    /// 这个值取决于素材的峰值桶有多细（采样率）、以及显示层的合并门槛，
    /// 视图模型两样都拿不到，自己推一遍只会推出第二份真相。
    /// </remarks>
    [ObservableProperty]
    private TimeSpan _minimumLyricSeparation;

    /// <summary>当前有没有歌词。</summary>
    public bool HasLyrics => _session.HasLyrics;

    /// <summary>
    /// 「绑定到此刻」这一刻能不能按。
    /// </summary>
    /// <remarks>
    /// 两个条件缺一不可：有音频才有播放头，而没有播放头就无所谓「此刻」；
    /// 「未绑定」栏里得有人，才有得盖。按钮与 <c>Enter</c> 都看这一处判据。
    /// </remarks>
    public bool CanStamp => HasAudio && UntimedLyrics.Count > 0;

    /// <summary>
    /// 打点预补偿：从「听到那一句」到「按下键」之间的那段反应时间。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>现在恒为 <see cref="TimeSpan.Zero"/>——功能尚未实现</b>（PLAN §8 里的
    /// <c>Shift+Enter</c> 与可配置预补偿）。先把它立在这里，是因为「盖章的目标时刻
    /// 怎么算出来」这条路径上只该有一个旋钮：将来设置界面写这个属性就够了，
    /// <see cref="LyricSession.StampTop"/> 与界面都不用改。
    /// </para>
    /// <para>
    /// 与另一种「延迟补偿」区分开：全局的那个（<c>GlobalOffsetMs</c>）加减在显示与导出上、
    /// 刻意不动打点值，于是随时能整体退回去；这里减掉的是一段真实存在的操作延迟，
    /// 它属于打点值本身，不是一层显示滤镜。
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private TimeSpan _stampCompensation;

    /// <summary>
    /// 有没有「会写进文件的内容」变了样。
    /// </summary>
    /// <remarks>
    /// 判据在 <see cref="LyricSession.HasUnsavedChanges"/>：拿要写出去的文本与上次保存的比。
    /// 粘贴进来的行不进 <c>.lrc</c>，所以粘贴<b>不算</b>未保存的改动——否则用户粘完歌词
    /// 想打开音频打轴时会发现：换音频被挡、保存又被拒（没有已打轴的行），
    /// 唯一的出路是把刚粘的东西丢掉。
    /// </remarks>
    public bool HasUnsavedLyrics => _session.HasUnsavedChanges;

    /// <summary>挡下「换音频」与「导入新歌词」时给用户的那句话。界面层与视图模型共用同一份措辞。</summary>
    public const string UnsavedLyricsHint =
        "歌词有未保存的改动 —— 先按 Ctrl+S 保存，或按 Ctrl+Shift+D 放弃改动";

    /// <summary>还没有主去处时的建议保存位置：与音频同名、同目录的 <c>.lrc</c>。</summary>
    public string? SuggestedLyricPath => LrcWriter.SuggestPath(_audioPath);

    /// <summary>保存的主去处；<c>null</c> 表示还没存过，第一次保存要问一次位置。</summary>
    public ILyricSink? SaveTarget => _session.Sink;

    // ------------------------------------------------------------ 载入与清空

    /// <summary>
    /// 用户指定的一份 <c>.lrc</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意做成同步：歌词文件通常只有几 KB，解析耗时在微秒量级，
    /// 为它走一趟线程池与 <c>await</c> 反而多出一次上下文切换。
    /// </para>
    /// <para>
    /// 失败时只更新状态栏，不动已经载入的音频，也不清掉上一次的歌词——
    /// 「导入了一份坏文件」不该把先前的工作成果一起抹掉。
    /// </para>
    /// <para>
    /// <b>成功时是整份替换。</b>导入一份 <c>.lrc</c> 的意思是「我改用这份歌词」，
    /// 于是手上原有的（包括粘贴进来还没打轴的文本）会被清掉，状态栏会把清掉的量说出来——
    /// 那是用户敲进去的东西，而它与时间轴不同，没有任何文件能把它找回来。
    /// </para>
    /// </remarks>
    public void LoadLyrics(string path)
    {
        try
        {
            var document = LyricImport.LoadFile(path, out var warning);
            var replaced = _session.LoadFrom(document, LyricOrigin.LrcFile, path, new LrcFileSink(path));

            PublishLyrics();
            StatusText = DescribeLoad($"已导入 {Path.GetFileName(path)}", document, replaced, warning);
        }
        catch (Exception ex)
        {
            StatusText = $"导入歌词失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 打开音频之后自动找一次侧车歌词（与音频同名、同目录的 <c>.lrc</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「随音频文件导入」。找不到是常态，一个字都不说；找到了就整个换上去，
    /// 并把它记成主去处——于是接着按 <c>Ctrl+S</c> 就是原地写回。
    /// </para>
    /// <para>
    /// 侧车之后本该还有一步「试音频内嵌的歌词」（<see cref="LyricImport.TryLoadEmbedded"/>），
    /// 那个接口已经立着、现在恒返回「尚未实现」，所以这里暂时不接。
    /// </para>
    /// </remarks>
    /// <param name="replaced">换音频时被清掉的旧歌词行数，一并说给用户听。</param>
    public void LoadSidecarLyrics(int replaced)
    {
        var document = LyricImport.TryLoadSidecar(_audioPath, out var path, out var warning);

        if (document is null)
        {
            if (warning is not null) StatusText = warning;
            else if (replaced > 0) StatusText += $" · 已清掉上一首的 {replaced} 行歌词";

            return;
        }

        var cleared = _session.LoadFrom(document, LyricOrigin.Sidecar, path, new LrcFileSink(path!));

        PublishLyrics();
        StatusText = DescribeLoad($"已随音频载入 {Path.GetFileName(path)}", document, cleared + replaced, warning);
    }

    /// <summary>
    /// 把一段粘贴进来的歌词文本放进「未绑定」栏。
    /// </summary>
    /// <remarks>
    /// 还没有歌词时就地开始一份新的：这正是「从零创建 <c>.lrc</c>」的入口，
    /// 此后它与导入来的歌词走的是同一条路（第一次保存时问一次位置，之后就原地写回）。
    /// </remarks>
    public void ImportLyricText(string text)
    {
        if (_session.Origin == LyricOrigin.None && _session.Document.Lines.Count == 0)
        {
            // 从零开始：把上一份的残留状态清干净，再往里加行。
            _session.Clear();
        }

        var count = _session.ImportText(text);

        if (count == 0)
        {
            StatusText = "没有可以导入的歌词 —— 每行一句，只有空行或空白";
            return;
        }

        PublishLyrics();

        var untimed = _session.UntimedLines;

        StatusText = $"已导入 {count} 行歌词到「未绑定」栏"
                   + (untimed.Count > 0 ? $" · 栈顶是「{Shorten(untimed[0].Text)}」" : string.Empty)
                   + " · Ctrl+Z 撤销";
    }

    /// <summary>
    /// 换音频时把上一首的歌词整个清掉。
    /// </summary>
    /// <remarks>
    /// <c>.lrc</c> 是音频的侧车文件，换了曲子之后旧歌词的时间轴毫无意义，留着只会在新波形上
    /// 画出一堆对不上的线；而用户自己粘进来的那几行也一并清掉——换音频就是换一份工作，
    /// 从干净状态开始。清掉的是他敲进去的文本，所以<b>调用方要把行数接过去说一声</b>
    /// （见 <see cref="LyricSession.Clear"/> 的返回值）。
    /// </remarks>
    private int ClearLyrics()
    {
        var cleared = _session.Clear();

        PublishLyrics();

        return cleared;
    }

    /// <summary>
    /// 用户主动清空歌词（<c>Ctrl+N</c>）。
    /// </summary>
    /// <remarks>
    /// 与换音频那条路不同：<b>这一步是可撤销的</b>，因为它整个走的是命令。
    /// 于是不必再弹一次「真的要清空吗」——按错了 <c>Ctrl+Z</c> 就回来了。
    /// </remarks>
    public void ClearLyricsByUser()
    {
        if (!_session.HasLyrics)
        {
            StatusText = "还没有歌词，没有可清空的内容";
            return;
        }

        var count = Document.Lines.Count;

        _session.Apply(new ClearLyricsCommand(Document));
        PublishLyrics();

        StatusText = $"已清空 {count} 行歌词 · Ctrl+Z 可以撤回";
    }

    // ------------------------------------------------------------ 编辑

    /// <summary>
    /// 把「未绑定」栏栈顶那一行绑到此刻的播放头位置——打轴里的<b>盖章</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「此刻」取的是播放指示线所在的时刻</b>（<see cref="PlayStartSeconds"/>），
    /// 也就是用户眼睛看到的那条琥珀色横线：按钮与 <c>Enter</c> 都盖在他瞄准的地方。
    /// 播放中这个值由波形控件按滚动位置每帧算出，与设备时钟相差不到一帧；
    /// 而若改用设备时钟，盖的就是「位置刚刷新到的那一刻」，可能比屏幕上那条线更靠前一点。
    /// </para>
    /// <para>
    /// 量化、错开、撤销全都交给 <see cref="LyricSession.StampTop"/>。这里只做界面层该做的
    /// 三件事：判断这一刻能不能盖、把两侧列表重新发布出去（盖章会把一行从未绑定搬到
    /// 已绑定，那是界面必须立刻看见的变化）、以及把结果说成一句人话。
    /// </para>
    /// </remarks>
    public void StampTopLyric()
    {
        if (!HasAudio)
        {
            StatusText = "请先打开音频文件，再打轴 —— 歌词要绑到这首曲子的时间轴上";
            return;
        }

        if (UntimedLyrics.Count == 0)
        {
            StatusText = "「未绑定」栏里没有等待打轴的歌词 —— 先按 Ctrl+T 贴一份歌词文本进来";
            return;
        }

        var at = PlayStartSeconds;

        // 有音频时它一定是有限的（控件会算出来）；这一句是防线，免得将来某条载入路径
        // 在半途按下了这个键，而 NaN 转 TimeSpan 会直接抛异常。
        if (!double.IsFinite(at))
        {
            StatusText = "播放头的位置还没算出来，稍等一下再打轴";
            return;
        }

        var stamp = _session.StampTop(
            TimeSpan.FromSeconds(at), MinimumLyricSeparation, StampCompensation);

        if (stamp is not { } report)
        {
            StatusText = "「未绑定」栏里没有等待打轴的歌词";
            return;
        }

        PublishLyrics();
        StatusText = DescribeStamp(report);
    }

    /// <summary>
    /// 把拖动产生的目标时刻写进文档，与别的歌词挨得太近的自动错开。
    /// </summary>
    /// <remarks>
    /// 拖动的全过程里时间轴数据一个字都没改，改动全都发生在松手这一下——
    /// 界面层因此不必在拖动期间反复重建绑定的列表，而「拖动中不重算合并与省略」
    /// 也成了免费的：那些判据只取决于时间码，时间码没动，它们算出来就还是原样。
    /// </remarks>
    public void ApplyTimeShift(IReadOnlyList<LyricTimeTarget> targets)
    {
        if (targets.Count == 0) return;

        var plan = LyricTimeline.Plan(targets, _session.TimedLines, MinimumLyricSeparation);

        if (plan.Changes.Count == 0)
        {
            // 拖回原位、或者根本没挪动。不动数据也不占一步撤销——
            // 否则用户按一次 Ctrl+Z 会看起来毫无反应。
            StatusText = "这几行没有移动，时间码没有变化";
            return;
        }

        _session.Apply(new TimeShiftCommand(plan.Changes));
        PublishLyrics();

        var parts = new List<string> { $"已调整 {plan.Report.Changed} 行歌词的时间码" };

        if (plan.Report.Nudged > 0)
        {
            parts.Add($"{plan.Report.Nudged} 行与相邻歌词过近，已自动错开"
                    + $"（最大 {plan.Report.LargestNudge.TotalMilliseconds:F0} 毫秒）");
        }

        parts.Add("Ctrl+Z 撤销 · Ctrl+S 保存");
        StatusText = string.Join(" · ", parts);
    }

    /// <summary>
    /// 把这几行移出时间轴，压回「未绑定」栏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 数据上只是把这些行的 <see cref="LyricLine.Start"/> 置回 <c>null</c>——于是它们自动
    /// 落进 <see cref="LyricDocument.UntimedLines"/>，也就是界面上那一栏；撤销则是把时刻写回去。
    /// <b>不必为此另立一个命令类型</b>：绑定与解绑在数据上都是「<c>Start</c> 从 A 变成 B」，
    /// 只是 A 或 B 恰好为 <c>null</c>。
    /// </para>
    /// <para>
    /// 移出的行<b>不会再写进 .lrc</b>（那里只写已打轴的行），这正是用户点这个箭头想要的结果。
    /// </para>
    /// </remarks>
    public void UnbindLyrics(IReadOnlyList<LyricLine> lines)
    {
        if (lines.Count == 0) return;

        var changes = new List<LyricTimeChange>(lines.Count);

        foreach (var line in lines)
        {
            // 没打轴的行进不了选区，这一句是防线：把它记成一次「从 null 变到 null」
            // 只会占掉一步毫无作用的撤销。
            if (line.Start is null) continue;

            changes.Add(new LyricTimeChange(line, line.Start, null));
        }

        if (changes.Count == 0)
        {
            StatusText = "选中的歌词本来就没有时间码，无需移出";
            return;
        }

        _session.Apply(new TimeShiftCommand(changes, $"移出 {changes.Count} 行歌词"));
        PublishLyrics();

        StatusText = $"已移出 {changes.Count} 行歌词 —— 回到「未绑定」栏 · Ctrl+Z 撤销";
    }

    /// <summary>
    /// 撤销一步。
    /// </summary>
    /// <remarks>
    /// 撤销只认 <see cref="IEditCommand"/>，不认「这是拖动还是盖章」——粘贴、清空、
    /// 将来编辑窗口那一整段改动各自做成一个命令之后，这里一个字都不用改。
    /// </remarks>
    public void Undo()
    {
        var label = _session.Undo();

        if (label is null)
        {
            StatusText = "没有可撤销的改动";
            return;
        }

        PublishLyrics();
        StatusText = $"已撤销：{label}";
    }

    /// <summary>重做一步。</summary>
    public void Redo()
    {
        var label = _session.Redo();

        if (label is null)
        {
            StatusText = "没有可重做的改动";
            return;
        }

        PublishLyrics();
        StatusText = $"已重做：{label}";
    }

    /// <summary>
    /// 改动落定之后统一收尾：作废派生缓存、重新发布、刷新界面文案。
    /// </summary>
    /// <remarks>
    /// 撤销 / 重做也走这里：命令只改数据，作废缓存与重新发布是调用方的事
    /// （见 <see cref="IEditCommand"/> 的说明）。放在一处，就不会出现「某条路径忘了作废」——
    /// 那种漏掉的表现是「时间改了、位置没动」，而且下一次改动还会拿着旧顺序继续算。
    /// </remarks>
    private void PublishLyrics()
    {
        Document.Invalidate();

        TimedLyrics = _session.TimedLines;
        UntimedLyrics = _session.UntimedLines;

        UpdateLyricsCaption();
        RefreshLyricsDetails();

        // 「有没有歌词」「脏不脏」「这一刻能不能盖章」都是算出来的属性，绑定的那一头
        // 收不到自动通知。漏掉 CanStamp 的表现是「栈里空了按钮还亮着」——按下去什么也不发生。
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(HasUnsavedLyrics));
        OnPropertyChanged(nameof(CanStamp));
    }

    // ------------------------------------------------------------ 保存与放弃

    /// <summary>
    /// 把当前内容写到某个去处。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「第一次保存」与「之后每次保存」在这里没有分支。</b>去处由界面层决定——
    /// 已经有主去处就直接用它，没有就问用户要一个（<see cref="SuggestedLyricPath"/>）。
    /// 写成功之后 <see cref="LyricSession.Sink"/> 就指了过去，于是「从零创建歌词」
    /// 在第一次保存之后与导入来的歌词再无区别。
    /// </para>
    /// <para>
    /// 写出去的内容只有已打轴的行（<see cref="LrcWriter.Format"/>）。一行时间码都没有时拒绝写盘：
    /// 那会写出一个空文件，而若已经有主去处，这一下就把硬盘上原有的歌词覆盖没了——
    /// 用户按 <c>Ctrl+S</c> 想要的是「存下我的工作」，不是「把我的歌词删掉」。
    /// </para>
    /// </remarks>
    public bool SaveLyrics(ILyricSink sink)
    {
        if (!_session.HasLyrics)
        {
            StatusText = "还没有歌词，没有可保存的内容";
            return false;
        }

        if (_session.TimedLines.Count == 0)
        {
            StatusText = _session.UntimedLines.Count > 0
                ? "没有需要保存的改动 —— .lrc 里只写绑到时间轴上的行，"
                + $"未绑定的 {_session.UntimedLines.Count} 行还留在「未绑定」栏"
                : "这份歌词里一行都没有，没有可保存的内容";

            return false;
        }

        try
        {
            var bytes = _session.Save(sink);

            UpdateLyricsCaption();
            RefreshLyricsDetails();

            var timed = _session.TimedLines.Count;
            var untimed = _session.UntimedLines.Count;

            // 未绑定的行确实没进文件，所以这里要说出来——否则用户看到「12 行已绑定」，
            // 会以为另外那 5 行也一起存下去了，直到下次打开文件才发现少了。
            var content = untimed > 0
                ? $"{timed} 行已绑定 · {untimed} 行未绑定（没写进文件）"
                : $"{timed} 行已绑定";

            StatusText = $"已保存 {sink.Describe} · {content} · {bytes / 1024.0:F1} KB";
            return true;
        }
        catch (Exception ex)
        {
            // 保存点与主去处都没动：下一次 Ctrl+S 还会往同一个地方写，不会悄悄地换个目的地。
            StatusText = $"保存失败（{sink.Describe}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 丢掉未保存的改动：按来源重新读一遍。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一步与撤销是两个层次：撤销只退一步，这里把整份改动全部丢掉。
    /// 刻意做成「重新读文件」而不是「留着一个空壳」：用户丢掉的是刚才那几下拖动，不是整份歌词。
    /// </para>
    /// <para>
    /// 粘贴来的歌词没有来源文件，于是这一步会把它整个清掉——与「导入 / 换音频都整份替换」
    /// 是同一个口径，状态栏也照实说。
    /// </para>
    /// </remarks>
    public void DiscardLyricEdits()
    {
        var path = _session.OriginPath;

        if (path is null || !File.Exists(path))
        {
            ClearLyrics();
            StatusText = "已放弃歌词（这份歌词没有来源文件，已整体清空）";
            return;
        }

        LoadLyrics(path);
        StatusText = "已放弃未保存的改动 —— 按来源文件重新载入";
    }

    // ------------------------------------------------------------ 界面上的呈现

    /// <summary>
    /// 盖完一章之后那句话：盖的是哪一行、落在哪儿、有没有被错开、还剩几行。
    /// </summary>
    /// <remarks>
    /// <b>「被错开」必须说出来。</b>用户瞄着指示线按下去，那一条却落在了旁边——
    /// 不说的话看上去就是打轴打偏了，而真正的原因（离相邻那条太近、连放到最大也分不开）
    /// 在屏幕上恰恰是看不见的。所以这里连「本来要盖在哪儿」也一并报出来。
    /// </remarks>
    private static string DescribeStamp(LyricStampReport report)
    {
        var parts = new List<string>
        {
            $"已绑定「{Shorten(report.Line.Text)}」到 {LrcTimecode.Format(report.Final)}",
        };

        if (report.Nudged)
        {
            parts.Add($"离相邻歌词太近，已自动错开 {Math.Abs(report.Nudge.TotalMilliseconds):F0} 毫秒"
                    + $"（本来要盖在 {LrcTimecode.Format(report.Wanted)}）");
        }

        parts.Add(report.Remaining > 0 ? $"栈里还剩 {report.Remaining} 行" : "栈里没有等待打轴的行了");
        parts.Add("Ctrl+Z 撤销");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 载入成功之后那句话：几行带时间戳、几行没有、清掉了多少旧行、有没有警告。
    /// </summary>
    /// <remarks>
    /// 三处载入（手动导入、随音频带、放弃改动后重读）共用这一份措辞，
    /// 于是它们说的永远是同一件事——尤其是「清掉了 N 行」这句：那清掉的是用户敲进去的文本，
    /// 任何一条载入路径漏说，都会表现成歌词<b>无声</b>少了一截。
    /// </remarks>
    private static string DescribeLoad(string prefix, LyricDocument document, int replaced, string? warning)
    {
        var parts = new List<string> { prefix };

        var timed = document.TimedLines.Count;
        var untimed = document.UntimedLines.Count;

        parts.Add(timed > 0 ? $"{timed} 行带时间戳" : "没有带时间戳的行");
        if (untimed > 0) parts.Add($"{untimed} 行没有时间戳，已放进「未绑定」栏");
        if (replaced > 0) parts.Add($"先前那 {replaced} 行已清掉");

        // offset 只提示、不折算，理由见 LrcMetadata.OffsetMilliseconds。
        // 这种事必须说出来：不说的话，用户会以为时间轴与播放对不上是本工具的毛病。
        if (document.Metadata.OffsetMilliseconds != 0)
        {
            parts.Add($"文件声明 offset {document.Metadata.OffsetMilliseconds:+#;-#;0} ms，"
                    + "本工具按原始时间戳显示、未折算");
        }

        if (warning is not null) parts.Add(warning);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 状态栏里引用一行歌词时用的短写法。
    /// </summary>
    /// <remarks>
    /// 状态栏是一行字，而歌词的一行可以很长（尤其是从网页上粘来的整段）。
    /// 截断只是为了让「栈顶是哪一句」这句话读得完，正文本身一个字都不会被改。
    /// </remarks>
    private static string Shorten(string text) => text.Length <= 14 ? text : text[..14] + "…";

    /// <summary>工具栏上那句「已绑定歌词 · N 行」，顺带把未保存状态说出来。</summary>
    private void UpdateLyricsCaption()
    {
        if (!_session.HasLyrics)
        {
            LyricsCaption = "已绑定歌词";
            return;
        }

        var text = $"已绑定歌词 · {_session.TimedLines.Count} 行";

        // 保存键是 Ctrl+S，但没有菜单也没有按钮，所以「有没有未保存的东西」
        // 只能靠这行字说。不说的话，用户不会知道自己该按什么。
        if (_session.HasUnsavedChanges) text += " · 未保存（Ctrl+S）";

        LyricsCaption = text;
    }

    /// <summary>把歌词那几行信息放进右栏，先摘掉上一次的。</summary>
    private void RefreshLyricsDetails()
    {
        for (var index = Details.Count - 1; index >= 0; index--)
        {
            if (Details[index].IsLyrics) Details.RemoveAt(index);
        }

        if (!_session.HasLyrics) return;

        var document = Document;
        var timed = document.TimedLines.Count;
        var untimed = document.UntimedLines.Count;

        var summary = untimed > 0
            ? $"{timed} 行已绑定 · {untimed} 行未绑定（不会写进 .lrc） · 共 {document.Lines.Count} 行"
            : $"{timed} 行已绑定 · 共 {document.Lines.Count} 行";

        summary += "\n" + DescribeOrigin();

        if (document.Metadata.Title is { Length: > 0 } title) summary += $"\n标题：{title}";
        if (document.Metadata.Artist is { Length: > 0 } artist) summary += $"\n艺术家：{artist}";

        Details.Add(new InfoRow("歌词", summary, IsLyrics: true));

        if (document.Metadata.OffsetMilliseconds != 0)
        {
            Details.Add(new InfoRow(
                "歌词 offset",
                $"{document.Metadata.OffsetMilliseconds:+#;-#;0} ms（文件声明；显示与导出均未折算）",
                IsLyrics: true));
        }
    }

    /// <summary>
    /// 这份歌词从哪儿来、往哪儿存。
    /// </summary>
    /// <remarks>
    /// 两者刻意分开说：另存为之后来源没变（<c>Ctrl+Shift+D</c> 要回它那儿重读），
    /// 而主去处变了——合成一句就没法把这件事讲清楚。
    /// </remarks>
    private string DescribeOrigin()
    {
        var origin = _session.Origin switch
        {
            LyricOrigin.Pasted => "来源：粘贴的歌词文本（没有来源文件）",
            LyricOrigin.Sidecar => $"来源：随音频载入的侧车歌词 {ShortenPath(_session.OriginPath)}",
            LyricOrigin.LrcFile => $"来源：导入的歌词文件 {ShortenPath(_session.OriginPath)}",
            LyricOrigin.Embedded => "来源：音频内嵌的歌词",
            _ => "来源：—",
        };

        var sink = _session.Sink is { } target
            ? $"保存到：{target.Describe}"
            : "保存到：尚未指定（第一次保存时会询问位置）";

        var dirty = _session.HasUnsavedChanges ? "（有未保存的改动）" : string.Empty;

        return $"{origin}\n{sink}{dirty}";
    }

    private static string ShortenPath(string? path) =>
        string.IsNullOrEmpty(path) ? "—" : Path.GetFileName(path);
}
