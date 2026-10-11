using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using SimpleLyrics.Core.Editing;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Controls;

/// <summary>
/// <see cref="LyricTrackView"/> 的交互部分：命中判据、选区、拖动与拖动预览。
/// </summary>
/// <remarks>
/// <para>
/// 与主文件合起来才是一个类（<c>partial</c>）。画面那半在 <c>LyricTrackView.cs</c>。
/// 分成两处只为读起来方便：一页是「怎么画」，一页是「点到谁、拖到哪」。
/// </para>
/// <para>
/// <b>入口都在外面。</b>本控件不参与命中测试（它盖在波形之上，参与了就会把波形整片挡掉），
/// 所以指针事件落在它下面那条 <c>Border</c> 上，由窗口的代码后置转成
/// <see cref="BeginGesture"/> / <see cref="ContinueGesture"/> / <see cref="EndGesture"/>。
/// </para>
/// <para>
/// <b>拖动期间一个字节都不改数据。</b>预览位置全在本文件里算，真正确立新时间码的是松手时
/// 发出去的 <see cref="TimesChanged"/>——数据层据此算出错开并做成一步可撤销的改动。
/// 于是「拖动中不重算合并与省略」是免费的：那些判据只看时间码，而时间码没动。
/// </para>
/// </remarks>
public sealed partial class LyricTrackView
{
    // ------------------------------------------------------------ 状态

    /// <summary>当前选中的歌词行。按引用比较——文档重排不会换掉行对象，选区因此能跨过拖动保住。</summary>
    private readonly HashSet<LyricLine> _selected = [];

    /// <summary>Shift 加选时的起点。</summary>
    private LyricLine? _selectionAnchor;

    /// <summary>框选开始前已有的选区；按住 Shift / Ctrl 框选时叠加在它上面。</summary>
    private LyricLine[]? _marqueeBase;

    /// <summary>正在纵向拖动选中的标识线。</summary>
    private bool _draggingMarks;

    /// <summary>正在拉矩形选区框。</summary>
    private bool _marqueeing;

    /// <summary>指针已经移动够了，这次操作是拖动而不是点击。</summary>
    private bool _moved;

    /// <summary>当前预览的位置会与别的歌词撞上（会被错开）。</summary>
    private bool _conflict;

    private double _pressY;
    private double _pointerY;

    /// <summary>
    /// 按下那一刻「指针底下」的时刻。
    /// </summary>
    /// <remarks>
    /// 存绝对时刻而不是按下点的纵坐标，是为了让播放中的自动滚动不把拖动弄乱：
    /// 每帧用「此刻指针下的时刻」减去它得到位移，于是波形一边滚动、被拖的那条线
    /// 一边牢牢贴在指针下面——这正是「拖拽不影响音频播放与波形滚动」需要的效果。
    /// 若改成累加每帧的像素位移，滚动带来的那一份位移就会凭空多出来。
    /// </remarks>
    private double _grabSeconds;

    // ------------------------------------------------------------ 对外

    /// <summary>选中的标识线数量变了。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>用户把选中的标识线拖到了新的时刻。</summary>
    public event EventHandler<LyricTimeChangeEventArgs>? TimesChanged;

    /// <summary>正在选中或拖动。</summary>
    public bool IsGestureActive => _draggingMarks || _marqueeing;

    /// <summary>当前选中的歌词行数。</summary>
    public int SelectionCount => _selected.Count;

    // ------------------------------------------------------------ 命中

    /// <summary>一簇（或其中一条）被画出来时，纵向覆盖到哪儿——命中测试用它来判断「点在这条歌词上」。</summary>
    private double LabelBottom(int index)
    {
        var cluster = _clusters[index];

        return cluster.Count == 1
            ? cluster.TopY + LabelOffset + _rows[index] * LineHeight
            : cluster.BottomY + LabelOffset + MeasureHeight("88 行", BlockFontSize);
    }

    /// <summary>
    /// 指针落在哪一簇上；<c>-1</c> 表示落在空白处。
    /// </summary>
    /// <remarks>
    /// 判据是「线上下各 <see cref="GrabTolerance"/> 像素，加上线下方这段标签」。
    /// 标签归上一条线所有：两条线之间的空白在视觉上就是上一条的标签区，
    /// 点在那里选中上一条，符合直觉。相邻抓取区重叠时取离线更近的那一条。
    /// </remarks>
    private int HitTest(double y)
    {
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var index = 0; index < _clusters.Count; index++)
        {
            var cluster = _clusters[index];

            if (y < cluster.TopY - GrabTolerance) continue;
            if (y > LabelBottom(index)) continue;

            var distance = Math.Abs(y - cluster.TopY);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = index;
        }

        return best;
    }

    // ------------------------------------------------------------ 手势

    /// <summary>
    /// 在已绑定歌词栏里按下鼠标。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三条分支：按在标识线上就（按修饰键）调整选区并准备拖动；按在空白处就拉选区框。
    /// 按在<b>已经选中</b>的标识线上时<b>不改动选区</b>——否则「把这一组一起拖走」
    /// 这件事根本做不出来，一按下去选区就只剩指针底下那一条了。
    /// </para>
    /// <para>
    /// 坐标是相对<b>本控件</b>的，也就是两栏共同的原点。窗口那一侧用
    /// <c>e.GetPosition(LyricMarks)</c> 取即可，不必再减去波形栏宽度——两栏本来就共用一个原点。
    /// </para>
    /// </remarks>
    /// <returns>是否接管了这次指针操作；<c>false</c> 表示这一栏此刻没什么可操作的。</returns>
    public bool BeginGesture(Point point, KeyModifiers modifiers)
    {
        if (Lyrics is not { Count: > 0 } lyrics) return false;
        if (point.X < Math.Max(TextInset, 0)) return false;

        BuildLayout();

        _pressY = point.Y;
        _pointerY = point.Y;
        _moved = false;
        _conflict = false;

        var hit = HitTest(point.Y);

        if (hit < 0)
        {
            _marqueeing = true;

            _marqueeBase = modifiers.HasFlag(KeyModifiers.Shift) || modifiers.HasFlag(KeyModifiers.Control)
                ? [.. _selected]
                : null;

            ApplyMarquee();
            InvalidateVisual();

            return true;
        }

        var cluster = _clusters[hit];
        var lines = LinesOf(lyrics, cluster);

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            _marqueeBase = null;

            // 整簇一起进出：色块代表的就是「这一段区间里的所有标识线」，只切换其中一条没有意义。
            var adding = !lines.All(_selected.Contains);
            foreach (var line in lines)
            {
                if (adding) _selected.Add(line);
                else _selected.Remove(line);
            }

            _selectionAnchor = lines[0];
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift) && _selectionAnchor is not null)
        {
            _marqueeBase = null;
            SelectRange(lyrics, _selectionAnchor, cluster);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!lines.Any(_selected.Contains))
        {
            _marqueeBase = null;
            _selected.Clear();

            foreach (var line in lines) _selected.Add(line);

            _selectionAnchor = lines[0];
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        _draggingMarks = true;
        _grabSeconds = ScrollSeconds + point.Y / Math.Max(PixelsPerSecond, 1e-6);

        InvalidateVisual();
        return true;
    }

    /// <summary>指针移动。选区框逐帧跟着走；拖动则要过了阈值才算数。</summary>
    public void ContinueGesture(Point point)
    {
        _pointerY = point.Y;

        if (_marqueeing)
        {
            ApplyMarquee();
            InvalidateVisual();
            return;
        }

        if (!_draggingMarks) return;

        // 没过阈值就还只是一次点击。拖动会真的改时间码，不能因为手抖一两个像素就动它。
        if (!_moved && Math.Abs(point.Y - _pressY) < ClickTolerance) return;

        _moved = true;
        _conflict = PreviewConflicts();

        InvalidateVisual();
    }

    /// <summary>
    /// 松开：真的拖过就把新时刻报出去，只是点了一下就什么也不改。
    /// </summary>
    /// <remarks>
    /// 报出去的时刻是<b>已量化到 LRC 时间码</b>的，与屏幕上显示的时间戳一致，
    /// 于是不存在「松手之后歌词跳了一下」。
    /// </remarks>
    public void EndGesture()
    {
        if (_marqueeing)
        {
            _marqueeing = false;
            _marqueeBase = null;

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();

            return;
        }

        if (!_draggingMarks) return;

        _draggingMarks = false;
        _conflict = false;

        if (_moved && _selected.Count > 0)
        {
            var targets = new List<LyricTimeTarget>(_selected.Count);

            foreach (var line in _selected) targets.Add(new LyricTimeTarget(line, PreviewTarget(line)));

            targets.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            TimesChanged?.Invoke(this, new LyricTimeChangeEventArgs(targets));
        }

        InvalidateVisual();
    }

    /// <summary>
    /// 放弃这次操作（<c>Esc</c>、以及别处抢走了指针捕获）。
    /// </summary>
    /// <remarks>
    /// 拖动过程中时间轴数据从未被改动，所以「拖错了能不能退回去」在这里是免费的：
    /// 放弃就是把预览丢掉，一个字都不用还原。
    /// </remarks>
    public void AbortGesture()
    {
        if (_marqueeing && _marqueeBase is not null)
        {
            _selected.Clear();
            foreach (var line in _marqueeBase) _selected.Add(line);

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        _marqueeing = false;
        _marqueeBase = null;
        _draggingMarks = false;
        _moved = false;
        _conflict = false;

        InvalidateVisual();
    }

    /// <summary>清空选区（<c>Esc</c>，或者文档换了）。</summary>
    public void ClearSelection()
    {
        if (_selected.Count == 0) return;

        _selected.Clear();
        _selectionAnchor = null;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ------------------------------------------------------------ 拖动预览

    /// <summary>这一行是不是正被拖走（拖动期间，被选中的行必然全都在被拖）。画面那半也用它。</summary>
    private bool IsLeaving(LyricLine line) => _draggingMarks && _selected.Contains(line);

    /// <summary>
    /// 被拖动的行从按下那一刻起偏移了多少时间。
    /// </summary>
    /// <remarks>
    /// 用「指针此刻所指的时刻」减去「按下那一刻指针所指的时刻」。波形在播放中会自己滚动，
    /// 这一个减法里就自动含进了那一份位移——否则被拖的线会从指针底下溜走。
    /// </remarks>
    private TimeSpan DragDelta =>
        TimeSpan.FromSeconds(ScrollSeconds + _pointerY / Math.Max(PixelsPerSecond, 1e-6) - _grabSeconds);

    /// <summary>某一行在当前预览下会落到哪个时刻（已量化）。</summary>
    private TimeSpan PreviewTarget(LyricLine line) =>
        LrcTimecode.Quantize((line.Start ?? TimeSpan.Zero) + DragDelta);

    /// <summary>某一行在当前预览下的纵坐标。</summary>
    private double PreviewY(LyricLine line) =>
        (PreviewTarget(line).TotalSeconds - ScrollSeconds) * Math.Max(PixelsPerSecond, 1e-6);

    /// <summary>
    /// 当前预览的位置会不会撞上别的歌词。
    /// </summary>
    /// <remarks>
    /// 判定用的是数据层那<b>同一份</b> <see cref="LyricTimeline.Resolve"/>：
    /// 若某一行算出来的落点与它想去的时刻不同，就说明会被错开。
    /// 显示层若自己写一套「够不够近」的判断，屏幕上的预警与实际落点迟早会分家。
    /// 这里只问不改——真的落点由数据层在松手时算。
    /// </remarks>
    private bool PreviewConflicts()
    {
        var lyrics = Lyrics;
        if (lyrics is null || _selected.Count == 0) return false;

        var targets = new List<LyricTimeTarget>(_selected.Count);
        foreach (var line in _selected) targets.Add(new LyricTimeTarget(line, PreviewTarget(line)));

        var resolved = LyricTimeline.Resolve(targets, lyrics, MinimumSeparation);

        for (var index = 0; index < targets.Count; index++)
        {
            if (LrcTimecode.Quantize(targets[index].Start) != resolved[index]) return true;
        }

        return false;
    }

    // ------------------------------------------------------------ 选区

    /// <summary>一簇里的全部歌词行。</summary>
    private static LyricLine[] LinesOf(IReadOnlyList<LyricLine> lyrics, in LyricMarkCluster cluster)
    {
        var lines = new LyricLine[cluster.Count];
        for (var index = 0; index < cluster.Count; index++) lines[index] = lyrics[cluster.Start + index];

        return lines;
    }

    /// <summary>从起点到某一簇整段选中（按时间顺序取下标区间）。</summary>
    private void SelectRange(IReadOnlyList<LyricLine> lyrics, LyricLine anchor, in LyricMarkCluster cluster)
    {
        var from = -1;
        for (var index = 0; index < lyrics.Count; index++)
        {
            if (!ReferenceEquals(lyrics[index], anchor)) continue;

            from = index;
            break;
        }

        if (from < 0) return;

        var start = Math.Min(from, cluster.Start);
        var end = Math.Max(from, cluster.End - 1);

        _selected.Clear();
        for (var index = start; index <= end; index++) _selected.Add(lyrics[index]);
    }

    /// <summary>拉一次选区框：把与矩形纵向范围相交的标识线选进来。</summary>
    private void ApplyMarquee()
    {
        var lyrics = Lyrics;
        if (lyrics is null) return;

        var top = Math.Min(_pressY, _pointerY);
        var bottom = Math.Max(_pressY, _pointerY);

        _selected.Clear();

        if (_marqueeBase is not null)
        {
            foreach (var line in _marqueeBase) _selected.Add(line);
        }

        for (var index = 0; index < _clusters.Count; index++)
        {
            var cluster = _clusters[index];

            // 线本身与它下面那段标签都算这一条的地盘：框住文字也算选中了它。
            if (cluster.TopY - GrabTolerance > bottom) continue;
            if (LabelBottom(index) < top) continue;

            for (var line = cluster.Start; line < cluster.End; line++) _selected.Add(lyrics[line]);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 文档换了（重新导入、换曲清空、拖动落定后的重排）：手里那些行可能已经不是同一条了。
        if (change.Property == LyricsProperty) PruneSelection();
    }

    /// <summary>
    /// 丢掉已经不在文档里的行，并收掉进行中的手势。
    /// </summary>
    /// <remarks>
    /// 拖动落定会让文档重排并重新发布列表，那时手势已经结束、行对象也没换，
    /// 于是选区<b>原样保住</b>——用户可以接着拖同一个选区。真正要清空的是
    /// 「导入了一份新歌词」这种换了行对象的情形。
    /// </remarks>
    private void PruneSelection()
    {
        if (IsGestureActive) AbortGesture();

        var lyrics = Lyrics;

        if (lyrics is null || lyrics.Count == 0)
        {
            _selected.Clear();
            _selectionAnchor = null;
            return;
        }

        var present = new HashSet<LyricLine>(lyrics);
        var removed = _selected.RemoveWhere(line => !present.Contains(line));

        if (_selectionAnchor is not null && !present.Contains(_selectionAnchor)) _selectionAnchor = null;

        if (removed > 0) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ 选区框的绘制

    /// <summary>
    /// 画矩形选区框。横向限在歌词栏内——框是「在已绑定歌词栏里拉的」，跑到波形上会误导。
    /// </summary>
    /// <remarks>
    /// 只此一处绘制留在这里而不是主文件：它画的就是「手指底下那件事」，
    /// 与选区框的几何、状态放在一起比跟色块和标签放在一起更合适。
    /// </remarks>
    private void DrawMarquee(DrawingContext context, double width, double height)
    {
        var top = Math.Min(_pressY, _pointerY);
        var bottom = Math.Max(_pressY, _pointerY);
        var left = Math.Max(TextInset, 0) + 2;

        var rect = new Rect(
            left,
            Math.Max(top, -CullMargin),
            Math.Max(width - left - 4, 1),
            Math.Max(Math.Min(bottom, height + CullMargin) - Math.Max(top, -CullMargin), 1));

        context.FillRectangle(MarqueeFillBrush, rect);
        context.DrawRectangle(null, MarqueePen, rect);
    }
}
