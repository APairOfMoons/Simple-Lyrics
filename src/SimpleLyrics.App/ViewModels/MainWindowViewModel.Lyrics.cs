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
/// 与主文件合起来才是一个类（<c>partial</c>）。那边是音频与播放，这边是歌词这一份文档
/// 以及它上面发生的改动——两块状态几乎不相干，只有「换音频要清掉歌词」一处交界，
/// 而那处是一个方法调用（<see cref="ClearLyrics"/>）。
/// </para>
/// <para>
/// 编辑一律走命令（<see cref="IEditCommand"/>）：<see cref="ApplyTimeShift"/> 算出前后值、
/// 压进 <see cref="TimelineEditHistory"/>、再重新发布。<see cref="Undo"/> / <see cref="Redo"/>
/// 只认那个接口，将来粘贴、盖章、批量偏移各自做成命令之后，这里一个字都不用改。
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel
{
    /// <summary>改动历史。撤销 / 重做只认它，见 <see cref="TimelineEditHistory"/>。</summary>
    private readonly TimelineEditHistory _history = new();

    /// <summary>上次保存（或刚载入）时历史停在哪儿；与当前位置不同就说明有未保存的改动。</summary>
    private int _savedPosition;

    // ------------------------------------------------------------ 歌词文档

    /// <summary>当前载入的歌词文档（含未打轴的行）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLyrics))]
    private LyricDocument? _lyrics;

    /// <summary>
    /// 已打轴的歌词行，按时间升序。绑定给波形上的歌词标识层。
    /// </summary>
    /// <remarks>
    /// 单独暴露一份，而不是让控件自己去 <see cref="Lyrics"/> 里筛选：
    /// 控件每次重绘都要用它，而筛选与排序是有成本的；更重要的是「按时间升序」是
    /// 标识层分簇算法的前提，把它交给数据层保证，比让控件每次自己排一遍可靠。
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<LyricLine> _timedLyrics = [];

    /// <summary>歌词栏的标题。</summary>
    [ObservableProperty]
    private string _lyricsCaption = "已绑定歌词";

    /// <summary>尚未导入歌词时歌词栏里的提示。</summary>
    [ObservableProperty]
    private string _lyricsHint = "尚未导入歌词\n\n点击工具栏的「导入歌词…」\n或按 Ctrl+I 载入 .lrc 文件";

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

    partial void OnMinimumLyricSeparationChanged(TimeSpan value) => RefreshLyricsDetails(Lyrics);

    /// <summary>是否已经导入歌词。</summary>
    public bool HasLyrics => Lyrics is not null;

    /// <summary>
    /// 歌词里有没有未保存的改动。
    /// </summary>
    /// <remarks>
    /// 有未保存改动时不许换音频：换曲要清掉整份歌词（旧时间轴对新曲子毫无意义），
    /// 而那等于把用户刚拖出来的改动一起丢掉。撤销栈救不了这一步，所以只能挡。
    /// </remarks>
    public bool HasUnsavedLyrics => Lyrics is { IsDirty: true };

    /// <summary>挡下「换音频」时给用户的那句话。界面层与视图模型共用同一份措辞。</summary>
    public const string UnsavedLyricsHint =
        "歌词有未保存的改动 —— 先按 Ctrl+S 保存，或按 Ctrl+Shift+D 放弃改动（然后重新打开音频）";

    /// <summary>还没有源文件时的建议保存位置：与音频同名、同目录的 <c>.lrc</c>。</summary>
    public string? SuggestedLyricPath => LrcWriter.SuggestPath(_audioPath);

    // ------------------------------------------------------------ 载入与清空

    /// <summary>
    /// 导入一份已经打好轴的 <c>.lrc</c>。
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
    /// </remarks>
    public void LoadLyrics(string path)
    {
        try
        {
            var document = LrcParser.Load(path, out var warning);
            document.SourcePath = path;

            var timed = document.TimedLines;
            var untimed = document.UntimedLines;

            // 换了一份歌词，上一条改动历史里的行对象全都不再属于当前文档。
            _history.Clear();
            _savedPosition = 0;

            Lyrics = document;
            TimedLyrics = timed;
            UpdateLyricsCaption();

            var parts = new List<string> { $"已导入 {Path.GetFileName(path)}" };

            parts.Add(timed.Count > 0 ? $"{timed.Count} 行带时间戳" : "没有带时间戳的行");
            if (untimed.Count > 0) parts.Add($"{untimed.Count} 行未打轴（打轴环节尚未实现）");

            // offset 只提示、不折算，理由见 LrcMetadata.OffsetMilliseconds。
            // 这种事必须说出来：不说的话，用户会以为时间轴与播放对不上是本工具的毛病。
            if (document.Metadata.OffsetMilliseconds != 0)
            {
                parts.Add($"文件声明 offset {document.Metadata.OffsetMilliseconds:+#;-#;0} ms，"
                        + "本工具按原始时间戳显示、未折算");
            }

            if (warning is not null) parts.Add(warning);

            StatusText = string.Join(" · ", parts);
            RefreshLyricsDetails(document);
        }
        catch (Exception ex)
        {
            StatusText = $"导入歌词失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 音频换了一个文件，把上一首的歌词清掉。
    /// </summary>
    /// <remarks>
    /// <c>.lrc</c> 是音频的侧车文件，换了曲子之后旧歌词的时间轴毫无意义，
    /// 留着只会在新波形上画出一堆对不上的线。这条路径只在
    /// <see cref="HasUnsavedLyrics"/> 为假时才会走到——见 <c>LoadAsync</c>。
    /// </remarks>
    private void ClearLyrics()
    {
        _history.Clear();
        _savedPosition = 0;

        Lyrics = null;
        TimedLyrics = [];
        LyricsCaption = "已绑定歌词";
        RefreshLyricsDetails(null);
    }

    // ------------------------------------------------------------ 编辑

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
        var document = Lyrics;
        if (document is null || targets.Count == 0) return;

        var plan = LyricTimeline.Plan(targets, document.TimedLines, MinimumLyricSeparation);

        if (plan.Changes.Count == 0)
        {
            // 拖回原位、或者根本没挪动。不动数据也不占一步撤销——
            // 否则用户按一次 Ctrl+Z 会看起来毫无反应。
            StatusText = "这几行没有移动，时间码没有变化";
            return;
        }

        _history.Push(new TimeShiftCommand(plan.Changes));
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
    /// 撤销一步。
    /// </summary>
    /// <remarks>
    /// 撤销只认 <see cref="IEditCommand"/>，不认「这是拖动还是盖章」——将来粘贴、盖章、
    /// 批量偏移各自做成一个命令之后，这里一个字都不用改。见 <see cref="TimelineEditHistory"/>。
    /// </remarks>
    public void Undo()
    {
        var label = _history.Undo();

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
        var label = _history.Redo();

        if (label is null)
        {
            StatusText = "没有可重做的改动";
            return;
        }

        PublishLyrics();
        StatusText = $"已重做：{label}";
    }

    /// <summary>
    /// 改动落定之后统一收尾：作废派生缓存、重新发布、刷新「有没有未保存的改动」。
    /// </summary>
    /// <remarks>
    /// <see cref="LyricDocument.IsDirty"/> 由历史的位置推出来，而不是设成一个一劳永逸的
    /// 布尔标志：这样「改一笔又撤回来」不会留下一个假的未保存标记——按标志位做的话，
    /// 撤销回去它也不知道该把标记清掉，于是「换音频」会被一直误挡。
    /// </remarks>
    private void PublishLyrics()
    {
        var document = Lyrics;
        if (document is null) return;

        // 时间码是就地改在行对象上的，排序缓存不会自己知道；不通知它，
        // 屏幕上就是「时间改了、位置没动」——而且下一条改动还会拿着旧顺序继续算。
        document.Invalidate();
        document.IsDirty = _history.Position != _savedPosition;

        TimedLyrics = document.TimedLines;
        UpdateLyricsCaption();
        RefreshLyricsDetails(document);
    }

    // ------------------------------------------------------------ 保存与放弃

    /// <summary>
    /// 把歌词写到指定路径。
    /// </summary>
    /// <remarks>
    /// <b>「有源文件」与「没有源文件」在这里没有分支。</b>路径由界面层决定——有源文件时
    /// 就用 <see cref="LyricDocument.SourcePath"/>，没有时问用户要一个——这个方法只管写。
    /// 写完之后 <see cref="LyricDocument.SourcePath"/> 就指了过去，于是将来
    /// 「从零创建歌词」在第一次保存之后与导入来的歌词再无区别。
    /// 落盘、改正来源、清掉未保存标记这三件事收在 <see cref="LrcWriter.Save"/> 一处。
    /// </remarks>
    public bool SaveLyrics(string path)
    {
        var document = Lyrics;

        if (document is null)
        {
            StatusText = "还没有载入歌词，没有可保存的内容";
            return false;
        }

        try
        {
            var bytes = LrcWriter.Save(document, path);

            // 保存点就是历史当前的位置：此后「改一笔再撤回」不会再显示未保存。
            _savedPosition = _history.Position;

            UpdateLyricsCaption();
            RefreshLyricsDetails(document);

            var timed = document.TimedLines.Count;
            var untimed = document.UntimedLines.Count;
            var content = untimed > 0 ? $"{timed} 行已绑定 · {untimed} 行未绑定" : $"{timed} 行已绑定";

            StatusText = $"已保存 {Path.GetFileName(path)} · {content} · {bytes / 1024.0:F1} KB";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 丢掉未保存的改动：按来源文件重新读一遍。
    /// </summary>
    /// <remarks>
    /// 这一步与撤销是两个层次：撤销只退一步，这里把整份改动全部丢掉。
    /// 刻意做成「重新读文件」而不是「清空歌词」：用户丢掉的是刚才那几下拖动，不是整份歌词。
    /// </remarks>
    public void DiscardLyricEdits()
    {
        var path = Lyrics?.SourcePath;

        if (path is null || !File.Exists(path))
        {
            ClearLyrics();
            StatusText = "已放弃歌词改动（这份歌词没有来源文件，改动已整体丢弃）";
            return;
        }

        LoadLyrics(path);
        StatusText = "已放弃未保存的改动 —— 按来源文件重新载入";
    }

    // ------------------------------------------------------------ 界面上的呈现

    /// <summary>工具栏上那句「已绑定歌词 · N 行」，顺带把未保存状态说出来。</summary>
    private void UpdateLyricsCaption()
    {
        var document = Lyrics;

        if (document is null)
        {
            LyricsCaption = "已绑定歌词";
            return;
        }

        var text = $"已绑定歌词 · {document.TimedLines.Count} 行";

        // 保存键是 Ctrl+S，但没有菜单也没有按钮，所以「有没有未保存的东西」
        // 只能靠这行字说。不说的话，用户不会知道自己该按什么。
        if (document.IsDirty) text += " · 未保存（Ctrl+S）";

        LyricsCaption = text;
    }

    /// <summary>把歌词那几行信息放进右栏，先摘掉上一次的。</summary>
    private void RefreshLyricsDetails(LyricDocument? document)
    {
        for (var index = Details.Count - 1; index >= 0; index--)
        {
            if (Details[index].IsLyrics) Details.RemoveAt(index);
        }

        if (document is null) return;

        var path = document.SourcePath;
        var timed = document.TimedLines.Count;
        var untimed = document.UntimedLines.Count;

        var summary = untimed > 0
            ? $"{timed} 行已绑定 · {untimed} 行未绑定 · 共 {document.Lines.Count} 行"
            : $"{timed} 行已绑定 · 共 {document.Lines.Count} 行";

        if (path is null) summary += "\n（尚无来源文件，第一次保存时会询问保存位置）";
        else summary += document.IsDirty ? $"\n{Path.GetFileName(path)}（有未保存的改动）" : $"\n{Path.GetFileName(path)}";

        if (document.Metadata.Title is { Length: > 0 } title) summary += $"\n标题：{title}";
        if (document.Metadata.Artist is { Length: > 0 } artist) summary += $"\n艺术家：{artist}";

        Details.Add(new InfoRow("歌词", summary, IsLyrics: true));

        if (MinimumLyricSeparation > TimeSpan.Zero)
        {
            var units = LyricTimeline.MinimumUnits(MinimumLyricSeparation);

            Details.Add(new InfoRow(
                "歌词最小间隔",
                $"{units * 10} 毫秒\n最大放大倍率下两条标识线不合并的最小距离"
                + $"（理论上限 {MinimumLyricSeparation.TotalMilliseconds:F2} 毫秒，"
                + "按 LRC 的 10 毫秒时间码向上取整）",
                IsLyrics: true));
        }

        if (document.Metadata.OffsetMilliseconds != 0)
        {
            Details.Add(new InfoRow(
                "歌词 offset",
                $"{document.Metadata.OffsetMilliseconds:+#;-#;0} ms（文件声明；显示与导出均未折算）",
                IsLyrics: true));
        }
    }
}
