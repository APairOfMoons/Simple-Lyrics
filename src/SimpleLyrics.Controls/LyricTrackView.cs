using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimpleLyrics.Core.Editing;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Controls;

/// <summary>
/// 歌词标识层：把每一行已打轴的歌词画成一条横线，并在线的<b>下方</b>排出「时间戳 + 正文」；
/// 同时承担这一栏里的选中与拖动（后者见 <c>LyricTrackView.Selection.cs</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么另起一层，而不是让两个控件各画一半。</b>线要连着横穿两栏，而栏与栏的分界
/// 正是控件的分界——并排的两个控件各自只画得出自己那一半，接缝处的对齐要靠两边分别算对，
/// 中间那条边框还会把线切断。改成在同一个 <c>Grid</c> 里让本控件横跨那两列，
/// 一条线就是一次绘制，接缝根本不存在。它盖在两层之上，因此不参与命中测试
/// （<see cref="IsHitTestVisible"/> 为 <c>false</c>），波形那边的拖动、缩放、滚动都不受影响。
/// </para>
/// <para>
/// <b>时间轴参数为什么必须从外面来。</b>本控件自己不知道波形滚到了哪儿，也不该知道：
/// 它和波形占用的必须是<b>完全相同的矩形</b>（同一行、同样的高度），任何一侧多出一点
/// 内边距或者标题栏，两栏里的线就会在同一时刻差出几个像素，看上去是断开的。
/// 所以位置一律由 <see cref="ScrollSeconds"/> 与 <see cref="PixelsPerSecond"/> 给定，
/// 而这两个值就是波形控件正在用的那两个。
/// </para>
/// <para>
/// <b>本层之内不再有任何固定不动的覆盖物。</b>栏目标题一度画在这里。它虽然只占顶端一小块，
/// 却与「随滚动经过那里的标签」必然打架：要么盖住标签，要么逼标签让位——而让位就是一个
/// 随滚动发生的显隐变化，看上去像闪烁。标题改由工具栏承担，这里只负责时间轴上的东西。
/// </para>
/// <para>
/// <b>交互逻辑在本层，事件的挂接在窗口那一侧。</b>本层不参与命中测试，所以指针事件落在的是
/// 它下面那条 <c>Border</c> 上；窗口的代码后置把「按下了 / 移动了 / 松开了」转成本层上的
/// <see cref="BeginGesture"/> / <see cref="ContinueGesture"/> / <see cref="EndGesture"/>，
/// 并把坐标换算到本层的坐标系里（两栏共用一个原点，于是这个换算就是零）。
/// 这样安排有两个好处：命中测试的边界不必依赖「控件只在一部分矩形内参与命中」这种
/// 运行期才知道的行为；而波形栏里的指针事件（拖动定位、拖动指示条）一个字都不用改——
/// 那一栏的按下根本不会走到这里来。
/// </para>
/// <para>
/// <b>本类分两个文件放。</b>这里是画面（布局、合并、排版、绘制），
/// <c>LyricTrackView.Selection.cs</c> 是交互（命中、选区、拖动、预览位置）。
/// 两者共用同一份状态，所以只是分文件，不是拆类——拆成两个类的话，
/// 「命中要看排版结果、绘制要看选区状态」这条双向依赖就得靠接口互相传，
/// 而两边的字段与算法本来是一件事。
/// </para>
/// </remarks>
public sealed partial class LyricTrackView : Control
{
    /// <summary>标识线的厚度（逻辑像素）。</summary>
    private const double MarkThickness = 2;

    /// <summary>选中、以及「即将被错开」时的线宽。粗一档本身就是「这些是活的」这个信号。</summary>
    private const double ActiveMarkThickness = 4;

    /// <summary>
    /// 相邻两条标识线至少拉开这么多像素，才不再逐条画线、改用一个色块盖住。
    /// </summary>
    /// <remarks>
    /// <b>这个门槛管的是「线本身能不能分辨」</b>，而不是「标签排得下几行」。
    /// 后者是每一对相邻标识线各自算的，见 <see cref="PlanLabel"/>。
    /// 它必须不小于 <see cref="RequiredRowGap"/>，否则两条单行标签会互相压住——
    /// 这条关系由 <c>SimpleLyrics.Preview</c> 每次运行核对。
    /// 它同时也是<b>最小间隔</b>的来源：见 <see cref="MinimumSeparationFor"/>。
    /// </remarks>
    public const double LabelMinGap = 22;

    private const double TextFontSize = 13;
    private const double BlockFontSize = 12.5;

    /// <summary>标签与标识线之间留出的空隙。</summary>
    private const double LabelOffset = 3;

    /// <summary>标签文字距歌词栏左边缘的距离。</summary>
    private const double LabelPadding = 14;

    /// <summary>第一行里，时间戳与正文之间的横向间隔。</summary>
    private const double TimestampSeparation = 8;

    /// <summary>时间戳最多占用的横向宽度，保证正文总有地方放。</summary>
    private const double TimestampMaxWidth = 96;

    /// <summary>为窄栏留出的下限：正文放不下这么多就只画时间戳。</summary>
    private const double MinimumTextWidth = 24;

    /// <summary>
    /// 逐行渲染时给宽度留的容差。
    /// </summary>
    /// <remarks>
    /// 换行时按 <c>width</c> 量、渲染时按 <c>width + 容差</c> 裁，是为了让「已经排下的整行」
    /// 绝不因为最后一次浮点取整而多出一个省略号。真正需要收省略号的是最后那一行，
    /// 它离上限还差得远，这点容差影响不到它。
    /// </remarks>
    private const double RowWidthTolerance = 2;

    /// <summary>整簇落在视口外这么多像素以内时仍然绘制，因为标签比线本身高得多。</summary>
    private const double CullMargin = 30;

    /// <summary>
    /// 标识线的纵向抓取范围：线上下一共这么多像素内都算「点到了这条歌词」。
    /// </summary>
    /// <remarks>
    /// 线只有 2 像素厚，而光标落点有抖动，要求像素级对准会变成一件很难受的事。
    /// 放宽到上下各 6 像素之后，相邻两条线的抓取区恰好相接而不重叠——
    /// 合并门槛是 22 像素，远大于两倍的这个值，所以真正挤在一起的那些本来就是同一个色块，
    /// 不存在「抓错了」的余地。
    /// </remarks>
    private const double GrabTolerance = 6;

    /// <summary>指针位移不超过这么多像素就只算「点了一下」，不动任何时间码。</summary>
    private const double ClickTolerance = 3;

    // ------------------------------------------------------- 「移出」箭头
    //
    // 选中的那几行右侧会浮出一个小箭头，点它就把这几行移出时间轴、压回未绑定栏。
    // 它是这一栏里唯一一个「按钮」，所以刻意长得像个按钮：实心圆底 + 白描边 + 白色箭头。

    /// <summary>箭头圆底的半径。</summary>
    private const double ArrowRadius = 9;

    /// <summary>箭头离栏右边缘的距离。</summary>
    private const double ArrowMargin = 10;

    /// <summary>
    /// 箭头的抓取半径，比圆底略大。
    /// </summary>
    /// <remarks>
    /// 它是个按钮，点起来不该要求像素级对准。放大 3 像素不会误伤别的操作——
    /// 箭头所在的这一小块地方本来就只有它一个可点的东西。
    /// </remarks>
    private const double ArrowHitRadius = ArrowRadius + 3;

    // ------------------------------------------------------------------ 外观
    //
    // 颜色分工（一个颜色只许有一个主人，否则用户没法靠颜色认东西）：
    //   粉  #F9A8D4  歌词本体（未选中的标识线与色块）
    //   白  #FFFFFF  我正在操作的那些标识线、以及矩形选区框
    //   灰  白色按不透明度铺开   选中的色块、以及色块里选中的那几条（填充，不是描线）
    //   红  #FF5C6C  「这么放会撞上别的歌词」，只在拖动途中出现
    //   琥珀 #FFC24A 播放指示器（独占，见下面 ActiveMarkPen 的说明）
    //   绿  #3FD9A0  音频起止界线（在波形控件里）
    //
    // 「色块里选中的那几条」刻意<b>不用白线</b>而用灰填充：块上那行「N 行 · 已选 M」是近白的，
    // 白线压在同一小块地方就与字形混成一片，分不出哪条是标识线哪段是字（实机反馈）。
    // 填充是一块区域，描线是一根笔画，两者在视觉上从来不混。

    private static readonly IPen MarkPen =
        new Pen(new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0xD4)), MarkThickness);

    /// <summary>
    /// 选中色：<b>白</b>。
    /// </summary>
    /// <remarks>
    /// 选中一度用的是播放指示器的琥珀色，理由是「琥珀=正在操作的对象」。实机一看就分不清：
    /// 两者都是横贯整栏的亮线，位置又在同一栏里，扫一眼根本认不出哪条是播放头。
    /// 改成白之后各自有了唯一的主人——粉是歌词本体、绿是音频起止、琥珀只剩播放指示器，
    /// 而白在这个界面上没被别的东西用过，在深底与波形上都够亮。
    /// </remarks>
    private static readonly IPen ActiveMarkPen = new Pen(new SolidColorBrush(Colors.White), ActiveMarkThickness);

    /// <summary>「这么放会撞上别的歌词」的预警色。只在拖动途中出现，且比粉色重得多，不会混。</summary>
    private static readonly IPen ConflictMarkPen =
        new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6C)), ActiveMarkThickness);

    /// <summary>选中标签背后的底色，像文本选区那样铺在字下面。</summary>
    private static readonly IBrush SelectionBandBrush = new SolidColorBrush(Colors.White, 0.10);

    private static readonly IBrush MarqueeFillBrush = new SolidColorBrush(Colors.White, 0.10);

    private static readonly IPen MarqueePen = new Pen(new SolidColorBrush(Colors.White, 0.85), 1);

    /// <summary>时间戳：信息量较低，用最淡的一档。</summary>
    private static readonly IBrush TimestampBrush =
        new SolidColorBrush(Color.FromRgb(0xE7, 0xB4, 0xD1));

    /// <summary>歌词正文：主角，用最亮的一档。深底上浅色字的可读性靠的是足够亮的字面。</summary>
    private static readonly IBrush TextBrush =
        new SolidColorBrush(Color.FromRgb(0xFD, 0xF2, 0xF8));

    /// <summary>色块上的条数文字。铺在粉底上。</summary>
    private static readonly IBrush BlockTextBrush =
        new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xF9));

    /// <summary>
    /// 铺在白底上的条数文字。
    /// </summary>
    /// <remarks>
    /// 整块都被选中时底色换成白，近白的字就没法读了。换个深色不是为了好看，
    /// 而是「底色换了，字色必须跟着换」这件事没法回避——否则那行「12 行」会整条糊掉。
    /// </remarks>
    private static readonly IBrush BlockTextOnActiveBrush =
        new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1D));

    private static readonly Typeface LabelTypeface = Typeface.Default;

    /// <summary>
    /// 「移出」箭头的圆底：<b>实心</b>。
    /// </summary>
    /// <remarks>
    /// 箭头浮在歌词文字之上（它在栏的右端，而正文一直排到那里），半透明底会让文字从箭头里
    /// 透出来，看上去像一团糊掉的墨。实心之后它就是一枚压在上面的按钮，谁也干扰不了谁。
    /// 颜色取的是比画布略亮一档的深色，于是它看得出是「一个东西」，又不刺眼。
    /// </remarks>
    private static readonly IBrush ArrowBackBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x27));

    /// <summary>圆底的描边，同时也是「白 = 我正在操作的那些」这条颜色分工的一部分。</summary>
    private static readonly IPen ArrowPen = new Pen(new SolidColorBrush(Colors.White, 0.85), 1);

    /// <summary>箭头本身的笔画。刻意比圆底描边粗，缩小到 18 像素的圆里仍然认得出来。</summary>
    private static readonly IBrush ArrowGlyphBrush = new SolidColorBrush(Colors.White);

    /// <summary>
    /// 色块的不透明度随簇内行数递增。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 色块本身就是「这里挤了多少行」的读数，本来不必靠文字才传达得出——
    /// 但透明度的分辨力有限（十行与四十行看着差不多），所以下面仍然会写一行条数。
    /// </para>
    /// <para>
    /// 上限刻意压在半透明附近。色块横跨两栏，而左栏是波形：那里正是一段音乐最密的地方，
    /// 一块近乎不透明的色板会把「这一段的音频长什么样」整个抹掉，
    /// 而打轴恰恰要靠它。宁可色块看着淡一些，也不能把波形盖没。
    /// </para>
    /// <para>
    /// 两族：粉的是普通色块，白（灰）的是<b>选中的部分</b>——整块选中时是整块底色，
    /// 部分选中时是块内那几条上的灰条。用同一张透明度阶梯，于是「选中」只换色相、不换浓淡。
    /// </para>
    /// </remarks>
    private static readonly IBrush[] BlockBrushes = BuildBlockBrushes(Color.FromRgb(0xF9, 0xA8, 0xD4));

    private static readonly IBrush[] ActiveBlockBrushes = BuildBlockBrushes(Colors.White);

    private static readonly IBrush[] BandBrushes = BuildBandBrushes();

    // ------------------------------------------------------------------ 状态

    /// <summary>上一帧的分簇结果，复用以免每帧重新分配。</summary>
    private readonly List<LyricMarkCluster> _clusters = [];

    /// <summary>每一簇的标签行数（色块记 1，反正用不到）。与 <see cref="_clusters"/> 一一对应。</summary>
    private readonly List<int> _rows = [];

    /// <summary>
    /// 换行结果缓存，键是「正文 + 每行可用宽度」。
    /// </summary>
    /// <remarks>
    /// 换行只取决于文字与栏宽，<b>与滚动位置和缩放都无关</b>——缩放只决定「取前几行」，
    /// 不改变行是怎么切的。所以这份缓存建一次可以一直用，只有栏宽变化（窗口缩放）才作废。
    /// 没有它的话，每一帧都要为每条可见歌词做若干次文本测量，而文本测量并不便宜。
    /// </remarks>
    private readonly Dictionary<(string Text, int Width), string[]> _wrapCache = [];

    /// <summary>换行缓存建立时所用的正文字段宽度。</summary>
    private double _wrapAvailable = double.NaN;

    /// <summary>本帧算出的正文字段宽度（见 <see cref="TextAreaWidth"/>）。</summary>
    private double _available;

    private static double? _lineHeight;

    // ------------------------------------------------------------ 依赖属性

    /// <summary>
    /// 要显示的歌词行。
    /// </summary>
    /// <remarks>
    /// <b>必须按时间升序</b>——分簇是一次单向扫描，顺序乱了簇就会算错。
    /// <see cref="LyricDocument.TimedLines"/> 已经保证了这一点。
    /// </remarks>
    public static readonly StyledProperty<IReadOnlyList<LyricLine>?> LyricsProperty =
        AvaloniaProperty.Register<LyricTrackView, IReadOnlyList<LyricLine>?>(nameof(Lyrics));

    /// <summary>纵向滚动位置（秒），与 <see cref="WaveformView.ScrollSeconds"/> 取同一个值。</summary>
    public static readonly StyledProperty<double> ScrollSecondsProperty =
        AvaloniaProperty.Register<LyricTrackView, double>(nameof(ScrollSeconds));

    /// <summary>纵向缩放（像素/秒），与 <see cref="WaveformView.PixelsPerSecond"/> 取同一个值。</summary>
    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<LyricTrackView, double>(nameof(PixelsPerSecond), 40d);

    /// <summary>
    /// 歌词栏在控件内的起始横坐标，也就是波形栏的宽度。
    /// </summary>
    /// <remarks>
    /// 标识线从 0 一直画到控件右边缘（横跨两栏），但文字只画在歌词栏这一侧——
    /// 压在波形上会挡住打轴时需要看的瞬态。这个值就是那条分界线。
    /// </remarks>
    public static readonly StyledProperty<double> TextInsetProperty =
        AvaloniaProperty.Register<LyricTrackView, double>(nameof(TextInset));

    /// <summary>
    /// 两条歌词之间至少留出的时间；拖到更近处会被错开。
    /// </summary>
    /// <remarks>
    /// 这个值由窗口那一侧从「最大放大倍率」算出来（见 <see cref="MinimumSeparationFor"/>），
    /// 是当前素材的性质而不是本控件的常数，所以走属性送进来。
    /// 本控件只用它来<b>预警</b>（把将要被错开的线画成另一个颜色），
    /// 真正的判定与错开在 <see cref="LyricTimeline"/> 里，两边用的是同一份实现。
    /// </remarks>
    public static readonly StyledProperty<TimeSpan> MinimumSeparationProperty =
        AvaloniaProperty.Register<LyricTrackView, TimeSpan>(nameof(MinimumSeparation));

    /// <summary>没有歌词时显示的提示文字。</summary>
    public static readonly StyledProperty<string?> EmptyHintProperty =
        AvaloniaProperty.Register<LyricTrackView, string?>(nameof(EmptyHint));

    static LyricTrackView()
    {
        AffectsRender<LyricTrackView>(
            LyricsProperty,
            ScrollSecondsProperty,
            PixelsPerSecondProperty,
            TextInsetProperty,
            MinimumSeparationProperty,
            EmptyHintProperty);
    }

    public LyricTrackView()
    {
        // 这一层只是画面。让它参与命中测试的话，波形上的拖动、缩放、滚动会被整片挡掉。
        IsHitTestVisible = false;
        Focusable = false;
    }

    public IReadOnlyList<LyricLine>? Lyrics
    {
        get => GetValue(LyricsProperty);
        set => SetValue(LyricsProperty, value);
    }

    public double ScrollSeconds
    {
        get => GetValue(ScrollSecondsProperty);
        set => SetValue(ScrollSecondsProperty, value);
    }

    public double PixelsPerSecond
    {
        get => GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public double TextInset
    {
        get => GetValue(TextInsetProperty);
        set => SetValue(TextInsetProperty, value);
    }

    public TimeSpan MinimumSeparation
    {
        get => GetValue(MinimumSeparationProperty);
        set => SetValue(MinimumSeparationProperty, value);
    }

    public string? EmptyHint
    {
        get => GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    // ------------------------------------------------------------ 度量

    /// <summary>正文一行的高度（像素），由当前字号实测得出。</summary>
    private static double LineHeight => _lineHeight ??= MeasureHeight("字", TextFontSize);

    /// <summary>
    /// 一条单行标签与下一条标识线之间至少需要的距离。
    /// </summary>
    /// <remarks>
    /// 这个值必须 ≤ <see cref="LabelMinGap"/>，否则合并门槛会放进两条互相压住的单行标签。
    /// 它与字号有关而门槛是手写的，所以由诊断工具核对，而不是靠人记。
    /// </remarks>
    public static double RequiredRowGap() => LabelOffset + LineHeight;

    /// <summary>
    /// 保证两条标识线<b>永不合并</b>的最小时间间隔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据是「在<b>最大放大倍率</b>下，两条线的屏幕间距刚好达到 <see cref="LabelMinGap"/>」——
    /// 也就是 <c>LabelMinGap ÷ 最大每秒像素数</c>。取最大放大倍率是因为那里间距最宽松：
    /// 连那一档都够不着的两条歌词，<b>无论怎么缩放都分不开</b>，在界面上就成了一条永远消失的行。
    /// 换句话说，这是整个工具的时间分辨率下限。
    /// </para>
    /// <para>
    /// 最大每秒像素数由波形控件给出（<see cref="WaveformView.MaximumZoom"/>），
    /// 与素材的采样率有关：44.1 kHz 下约 1378 像素/秒，于是 22 像素对应 <b>15.96 毫秒</b>；
    /// 48 kHz 下约 1500 像素/秒，对应 14.67 毫秒。而 LRC 只存得到 10 毫秒，
    /// 所以实际执行的间隔会被向上取整到 20 毫秒（见
    /// <see cref="LyricTimeline.MinimumUnits"/>）。
    /// </para>
    /// <para>
    /// 公开成静态函数、参数只有每秒像素数，是为了让诊断工具能用<b>同一个判据</b>核对：
    /// 它若自己再写一遍「22 除以多少」，改天门槛或线宽一动就悄悄脱节。
    /// </para>
    /// </remarks>
    public static TimeSpan MinimumSeparationFor(double maximumPixelsPerSecond) =>
        TimeSpan.FromSeconds(LabelMinGap / Math.Max(maximumPixelsPerSecond, 1e-6));

    // ------------------------------------------------------------ 分簇与排版

    /// <summary>
    /// 把歌词按「在屏幕上是否挤在一起」分组。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判据为什么是像素而不是秒。</b>相邻两行的屏幕间距是 <c>Δt × 每秒像素数</c>，
    /// 其中 <c>Δt</c> 只与歌词本身有关、与滚动位置无关——于是同一簇在滚动过程中
    /// 既不会裂开也不会粘上，只是整体平移，观感稳定。若改用「时间差小于某个秒数」，
    /// 滚动确实也稳定，但缩放就失去了作用：放大之后线明明已经拉得很开，
    /// 却仍然按时间被判为密集，色块该散而不散。
    /// </para>
    /// <para>
    /// <b>分组必须基于全部歌词，而不是「屏幕内的那些」。</b>视口顶端那条线可能与
    /// 屏幕外一点点的下一条同属于一簇，只按可见部分分组，滚动时边界处的簇会时而多算一行、
    /// 时而少算一行，色块的高度就会一跳一跳。
    /// </para>
    /// <para>
    /// <b>「与滚动无关」这一条是拖动功能的地基。</b>拖动过程中不重新计算合并与省略，
    /// 靠的不是冻结一份快照，而是：时间轴数据在松手之前根本没有被改动，而分组与行数
    /// 又只取决于间距 —— 于是这一帧算出来的和按下之前逐字相同。缩放则必须在此期间锁住
    /// （见 <c>WaveformView.ZoomLocked</c>），因为它是唯一能让间距改变的东西。
    /// </para>
    /// <para>
    /// 公开出来是为了让诊断工具用<b>同一份</b>实现核对合并规律。工具若自己再写一遍划分，
    /// 两边迟早会分家，那时测出来的就不是屏幕上发生的事。
    /// </para>
    /// </remarks>
    /// <param name="destination">结果写到这里，先清空。由调用方持有，可以逐帧复用。</param>
    /// <param name="lines">
    /// 已按时间升序排列的歌词行，且<b>全部已打轴</b>（<see cref="LyricDocument.TimedLines"/> 即是）。
    /// 簇用「起始下标 + 行数」表示，那要求一簇之内的行在列表里是连续的一段。
    /// </param>
    /// <param name="scrollSeconds">控件顶端对应的时刻。</param>
    /// <param name="pixelsPerSecond">纵向缩放。</param>
    /// <param name="minimumGapPixels">小于这个间距就并进同一簇。</param>
    public static void GroupInto(
        List<LyricMarkCluster> destination,
        IReadOnlyList<LyricLine> lines,
        double scrollSeconds,
        double pixelsPerSecond,
        double minimumGapPixels)
    {
        destination.Clear();
        if (lines.Count == 0) return;

        var pixels = Math.Max(pixelsPerSecond, 1e-6);
        var gap = Math.Max(minimumGapPixels, 0);

        var top = 0.0;
        var bottom = 0.0;
        var start = 0;
        var count = 0;
        var previousY = 0.0;

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Start is not { } time) continue;

            var y = (time.TotalSeconds - scrollSeconds) * pixels;

            if (count > 0 && y - previousY >= gap)
            {
                destination.Add(new LyricMarkCluster(top, bottom, start, count));
                count = 0;
            }

            if (count == 0)
            {
                top = y;
                start = index;
            }

            bottom = y;
            count++;
            previousY = y;
        }

        if (count > 0) destination.Add(new LyricMarkCluster(top, bottom, start, count));
    }

    /// <summary>
    /// 一条标签实际会占几行、有没有被省略。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>行数只由「到<b>下一条</b>标识线的距离」决定。</b>标签整个排在线的下方，
    /// 于是它唯一可能压住的东西就是下一条歌词——而前一条的标签之所以没有压到这一条，
    /// 是因为它自己算行数时用的正是「到这一条的距离」。一个判据同时管住了两头，
    /// 不需要再回头看。
    /// </para>
    /// <para>
    /// 这条判据同样只看间距，因此只随缩放变化，不随滚动变化：同一条歌词从进入视口到
    /// 离开视口，行数一次都不会变。若改成按绝对纵坐标判断（「快到底了就少画两行」），
    /// 标签会在滚动途中自己长出一行或收回去，看上去正是那种最难解释的抖动。
    /// </para>
    /// <para>
    /// 公开出来是给诊断工具用的：它要统计「有多少条完整、有多少条被省略」，就得与控件
    /// 用同一个判据。工具自己再推一遍的话，测的就不是屏幕上真正发生的事。
    /// </para>
    /// </remarks>
    /// <param name="line">这一条标识线对应的歌词。</param>
    /// <param name="available">正文字段的宽度（见 <see cref="TextAreaWidth"/>）。</param>
    /// <param name="room">从这条标识线到<b>下一条</b>的纵向距离（像素）；没有下一条时给无穷大。</param>
    public static LyricLabelPlan PlanLabel(LyricLine line, double available, double room)
    {
        var needed = NeededLines(line, available);
        var rows = RowsFor(room, needed);

        return new LyricLabelPlan(rows, rows < needed);
    }

    /// <summary>
    /// 正文在给定栏宽下完整排下来需要几行。
    /// </summary>
    /// <remarks>
    /// 时间戳在整段文字左侧占去一条固定的留白（悬挂缩进），因此<b>每一行</b>的可用宽度
    /// 都是「字段宽 − 时间戳宽 − 间隔」，而不只是第一行。折行必须照这个宽度切，
    /// 否则续行会溢到时间戳那一列上。
    /// </remarks>
    public static int NeededLines(LyricLine line, double available)
    {
        if (line.Text.Length == 0) return 1;

        var width = BodyWidth(line.Start ?? TimeSpan.Zero, available);
        return width < MinimumTextWidth ? 1 : WrapText(line.Text, width).Length;
    }

    /// <summary>
    /// 正文字段在控件里实际占的宽度。
    /// </summary>
    /// <remarks>
    /// 公开出来，是为了让诊断工具算出的「需要几行」与屏幕上真正发生的事一致——
    /// 那几个内边距都是本控件的私事，工具抄一份数字迟早会抄漏。
    /// </remarks>
    public static double TextAreaWidth(double controlWidth, double textInset) =>
        controlWidth - (Math.Max(textInset, 0) + LabelPadding) - 10;

    private static int RowsFor(double room, int neededLines) =>
        Math.Clamp((int)Math.Floor(room / LineHeight), 1, Math.Max(neededLines, 1));

    /// <summary>把当前滚动与缩放下的分簇与行数算一遍，供绘制与命中测试共用。</summary>
    private void BuildLayout()
    {
        var lyrics = Lyrics ?? [];

        GroupInto(_clusters, lyrics, ScrollSeconds, Math.Max(PixelsPerSecond, 1e-6), LabelMinGap);

        _available = TextAreaWidth(Bounds.Width, TextInset);

        _rows.Clear();

        for (var index = 0; index < _clusters.Count; index++)
        {
            var cluster = _clusters[index];

            // 色块不排标签，记 1 只是为了下面「部分选中时剩下一条」那种情形有个合法值。
            _rows.Add(cluster.Count == 1
                ? PlanLabel(lyrics[cluster.Start], _available, RoomOf(index)).Rows
                : 1);
        }
    }

    /// <summary>这一簇到下一条标识线的纵向距离。末条给无穷空间——它画多长都不会压住任何东西。</summary>
    private double RoomOf(int index) =>
        index + 1 < _clusters.Count
            ? _clusters[index + 1].TopY - _clusters[index].TopY - LabelOffset
            : double.PositiveInfinity;

    /// <summary>某一行在当前滚动与缩放下的纵坐标。</summary>
    private double MarkY(LyricLine line) =>
        ((line.Start ?? TimeSpan.Zero).TotalSeconds - ScrollSeconds) * Math.Max(PixelsPerSecond, 1e-6);

    // ------------------------------------------------------------ 绘制

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        // 与波形控件同样的理由：本层盖在相邻两栏之上，首尾两行与落在边缘的标签都可能越出边界，
        // 不夹住的话会画到工具栏和状态栏上去。
        using (context.PushClip(new Rect(0, 0, width, height)))
        {
            RenderContent(context, width, height);
        }
    }

    private void RenderContent(DrawingContext context, double width, double height)
    {
        var lyrics = Lyrics;

        if (lyrics is null || lyrics.Count == 0)
        {
            DrawEmptyHint(context, width, height);
            return;
        }

        BuildLayout();

        // 栏宽变了，之前算的换行全部作废。
        if (_wrapAvailable != _available)
        {
            _wrapCache.Clear();
            _wrapAvailable = _available;
        }

        var textX = Math.Max(TextInset, 0) + LabelPadding;

        for (var index = 0; index < _clusters.Count; index++)
        {
            DrawCluster(context, width, height, lyrics, _clusters[index], _rows[index], textX);
        }

        if (_marqueeing) DrawMarquee(context, width, height);

        // 箭头的圆底是实心的，必须画在最后：它要盖住压在它底下的那几个字。
        DrawUnbindArrow(context, height);
    }

    /// <summary>
    /// 把一簇画出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>形状只由时间码决定，与「谁被选中」无关。</b>这一点是被实机反馈逼出来的：
    /// 早先的写法在「簇里既有选中的、又有没选中的」时把色块拆开画，于是把一个色块拖到
    /// 另一个色块附近松手之后，新合并出来的那一簇当场散成一条条线，**非得取消选择才重新合并**——
    /// 看上去像是合并算错了，其实是画法按选区走了。现在选中的一条在簇内部被标出来
    /// （白线 / 白底），整簇仍然是一整块。
    /// </para>
    /// <para>
    /// 拖动中拆成两体：被拖走的那几条（跟着指针）和留在原处的那一部分。这个拆分来自
    /// 「哪些行正在被拖」，而不是来自选区本身——拖动期间被选中的行必然全都在被拖，
    /// 两者恰好重合，而松手之后「正在被拖」整个消失，簇立刻恢复成完整的一体。
    /// </para>
    /// </remarks>
    private void DrawCluster(
        DrawingContext context,
        double width,
        double height,
        IReadOnlyList<LyricLine> lyrics,
        in LyricMarkCluster cluster,
        int rows,
        double textX)
    {
        var leaving = 0;

        for (var index = cluster.Start; index < cluster.End; index++)
        {
            if (IsLeaving(lyrics[index])) leaving++;
        }

        if (leaving < cluster.Count)
        {
            DrawUnit(context, width, height, lyrics, cluster, rows, textX, draggedUnit: false);
        }

        if (leaving > 0)
        {
            DrawUnit(context, width, height, lyrics, cluster, rows, textX, draggedUnit: true);
        }
    }

    /// <summary>
    /// 画一簇里的「一体」：留在原处的那部分，或者正被拖走的那部分。
    /// </summary>
    /// <param name="draggedUnit">
    /// 画的是被拖走的那一体吗？<c>false</c> 表示画留在原处的那一体（不拖动时就是整簇）。
    /// </param>
    private void DrawUnit(
        DrawingContext context,
        double width,
        double height,
        IReadOnlyList<LyricLine> lyrics,
        in LyricMarkCluster cluster,
        int rows,
        double textX,
        bool draggedUnit)
    {
        var count = 0;
        var active = 0;
        var top = double.PositiveInfinity;
        var bottom = double.NegativeInfinity;
        LyricLine? lone = null;

        for (var index = cluster.Start; index < cluster.End; index++)
        {
            var line = lyrics[index];

            if (IsLeaving(line) != draggedUnit) continue;

            count++;
            lone = line;

            if (_selected.Contains(line)) active++;

            // 被拖的按预览位置，其余按时间码算出来的位置；后者在松手之前与拖动开始时逐像素相同。
            var y = draggedUnit ? PreviewY(line) : MarkY(line);

            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }

        if (count == 0) return;
        if (bottom < -CullMargin || top > height + CullMargin) return;

        if (count == 1 && lone is not null)
        {
            var selected = _selected.Contains(lone);

            if (selected) DrawSelectionBand(context, textX, top, top + LabelOffset + rows * LineHeight);

            DrawMark(context, width, top, selected);

            if (_available < MinimumTextWidth * 2) return;

            // 标签行数用的是这一簇的排版结果（来自时间码），拖动期间不重算——
            // 这正是「拖动中不重算省略」的落点：被拖的那条标签该几行还是几行。
            DrawSingleLabel(context, textX, _available, rows, top, lone);
            return;
        }

        // 多条挤在一起：连同两端的线一起盖住，读起来就是「这一段全是歌词」。
        var blockTop = top - MarkThickness / 2;
        var blockBottom = bottom + MarkThickness / 2;
        var blockHeight = Math.Max(blockBottom - blockTop, MarkThickness);

        var wholeActive = active == count;

        context.FillRectangle(
            wholeActive ? ActiveBlockBrushFor(count) : BlockBrushFor(count),
            new Rect(0, blockTop, width, blockHeight),
            2);

        // 部分选中：在块内把选中的那几条单独标出来。色块本身看不出「这几条是我的」，
        // 而拖动落定之后最想知道的就是这件事——它往往刚和邻居合并成一块。
        //
        // 标法用<b>填充</b>而不是描线：白线会和块上那行近白的字混在一起，两样都白、
        // 又都在同一小块地方，眼睛分不出哪条是「选中的标识线」哪段是「文字」。
        // 换成与「整块选中」同一族的灰之后，它是"一块浅色区域"，与字形再也不会混淆。
        if (active > 0 && !wholeActive)
        {
            for (var index = cluster.Start; index < cluster.End; index++)
            {
                var line = lyrics[index];
                if (IsLeaving(line) != draggedUnit || !_selected.Contains(line)) continue;

                var y = draggedUnit ? PreviewY(line) : MarkY(line);

                // 夹进色块之内：灰条的边框不探出块外，块的轮廓因此仍只由时间码决定。
                var bandTop = Math.Max(y - ActiveMarkThickness / 2, blockTop);
                var bandBottom = Math.Min(y + ActiveMarkThickness / 2, blockBottom);

                if (bandBottom <= bandTop) continue;

                context.FillRectangle(
                    BandBrushFor(count),
                    new Rect(0, bandTop, width, bandBottom - bandTop));
            }
        }

        if (_available < 48) return;

        var caption = active > 0 && !wholeActive ? $"{count} 行 · 已选 {active}" : $"{count} 行";

        // 底色浅就用深字：这一条现在按<b>不透明度</b>定，而不是按「整块是否选中」定。
        // 部分选中的块里，灰条与粉底是同一档不透明度，所以字压在谁上面亮度都一样——
        // 判断跟着不透明度走，两种情形就都读得清。
        var lightBackdrop = wholeActive || BlockAlpha(count) >= 0x60;
        var label = NewText(caption, lightBackdrop ? BlockTextOnActiveBrush : BlockTextBrush, BlockFontSize, _available);

        // 色块装不下字就不写。硬塞进去只会变成压在色块上的一团噪点，
        // 而「这里很密」这件事色块本身已经说清楚了。
        //
        // 但门槛刻意放到「文字高度减 2」而不是「文字高度加 2」：两行的色块高约 18 像素，
        // 而一行字高约 16.6，卡在「加 2」上正好写不进去——被丢掉的偏偏是「已选 1」这几个字，
        // 而那正是「这一块里哪几条是我的」唯一的文字线索。让字最多探出边界 1 像素，
        // 视觉上什么也没损失，信息却保住了。
        if (blockHeight < label.Height - 2) return;

        context.DrawText(label, new Point(textX, (blockTop + blockBottom) / 2 - label.Height / 2));
    }

    private void DrawMark(DrawingContext context, double width, double y, bool selected)
    {
        context.DrawLine(
            selected ? (_conflict ? ConflictMarkPen : ActiveMarkPen) : MarkPen,
            new Point(0, y),
            new Point(width, y));
    }

    /// <summary>
    /// 画那个「把选中的这几行移出时间轴」的箭头。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要它。</b>拖动能改时刻，但改不出「这一行不该在这份歌词里」这件事：
    /// 用户可能发现某一行是从别处误粘进来的、或者唱段其实不存在。此前唯一的办法是
    /// 保存之后用别的编辑器删——那等于让本工具在最常见的清理动作上缺席。
    /// </para>
    /// <para>
    /// <b>为什么只画一个，而不是每个选中的簇各画一个。</b>一次操作的对象是<b>整个选区</b>，
    /// 画多个箭头会让人以为「点哪个就移出哪个」。整个选区一个箭头，位置取选区在屏幕上的
    /// 纵向中点，箭头与「这几行」之间的对应关系因此是唯一的。
    /// </para>
    /// <para>
    /// <b>箭头用实心图形，不描轮廓。</b>实心块内部一定有整片的纯白像素，而 1 像素级的描边
    /// 在抗锯齿下几乎每个像素都是混出来的颜色——「这个按钮在不在、在什么高度」这件事
    /// 由诊断工具数纯白像素来核对（<c>Preview</c> 的 <c>MeasureUnbindArrow</c>），
    /// 画法得让那条断言数得到东西。
    /// </para>
    /// </remarks>
    private void DrawUnbindArrow(DrawingContext context, double height)
    {
        if (ArrowCenter(height) is not { } center) return;

        context.DrawEllipse(ArrowBackBrush, ArrowPen, center, ArrowRadius, ArrowRadius);

        // 箭头 = 一根横杆 + 一个实心三角，合起来是一个右向箭头。
        context.FillRectangle(ArrowGlyphBrush, new Rect(center.X - 4, center.Y - 1, 7, 2));

        var head = new StreamGeometry();

        using (var figure = head.Open())
        {
            figure.BeginFigure(new Point(center.X + 4, center.Y), true);
            figure.LineTo(new Point(center.X - 1, center.Y - 4));
            figure.LineTo(new Point(center.X - 1, center.Y + 4));
            figure.EndFigure(true);
        }

        context.DrawGeometry(ArrowGlyphBrush, null, head);
    }

    /// <summary>选中项背后的那块底色，像文本选区那样铺在字下面。</summary>
    private void DrawSelectionBand(DrawingContext context, double textX, double top, double bottom)
    {
        if (bottom <= top) return;

        var rect = new Rect(textX - 7, top - 3, Math.Max(_available + 10, 12), bottom - top + 6);

        context.FillRectangle(SelectionBandBrush, rect, 3);
    }

    /// <summary>
    /// 画一条歌词的标签：时间戳打头，正文接在后面，排不下就往下面折行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>时间戳不是单独占一行的。</b>它跟在正文前面，于是「只有一行可放」与
    /// 「可以放五行」是同一个版式的两种参数，而不是两套排版——缩放时看到的是文字
    /// 一行行长出来，而不是版式忽然换了一种。
    /// </para>
    /// <para>
    /// <b>续行与第一行的正文左对齐（悬挂缩进）。</b>时间戳那一列成为整段文字左侧的留白，
    /// 而不是只影响第一行。若让续行顶着栏的左边缘排，整段文字会显得像是「第一行缩进了、
    /// 后面几行忘了缩进」——读起来是碎的，而文字对齐本来就是让人一眼看出「这几条属于同一条」。
    /// 代价是每一行都少用一点宽度，换来的是整块文字左边缘齐平。
    /// </para>
    /// <para>
    /// <b>最后一行借用 <see cref="TextTrimming.CharacterEllipsis"/> 收尾。</b>
    /// 前面几行都是按可用宽度切好的整行，只有决定停笔的那一行需要标出「后面还有」。
    /// </para>
    /// </remarks>
    private void DrawSingleLabel(
        DrawingContext context,
        double textX,
        double available,
        int rows,
        double lineY,
        LyricLine line)
    {
        var timestamp = LrcTimecode.Format(line.Start ?? TimeSpan.Zero);
        var stamp = NewText(timestamp, TimestampBrush, TextFontSize, Math.Min(available, TimestampMaxWidth));

        var bodyWidth = available - stamp.Width - TimestampSeparation;
        var body = line.Text;

        // 栏窄到放不下正文，或者这行本来就没有正文：只画时间戳。
        if (bodyWidth < MinimumTextWidth || body.Length == 0)
        {
            context.DrawText(stamp, new Point(textX, lineY + LabelOffset));
            return;
        }

        var lines = Wrapped(body, bodyWidth);

        // 拖动预览一律只给 1 行，所以这里必须夹住：不能因为传进来的是 1 就去访问 lines[1]。
        rows = Math.Clamp(rows, 1, lines.Length);

        // 最后一行取「从这一行开头起剩下的全部正文」，交给 Trimming 去收省略号；
        // 上面几行都是已经切好的整行，按原样画即可。
        var lastStart = 0;
        for (var row = 0; row < rows - 1; row++) lastStart += lines[row].Length;

        var tail = body[lastStart..];
        var bodyX = textX + stamp.Width + TimestampSeparation;

        for (var row = 0; row < rows; row++)
        {
            var content = row == rows - 1 ? tail : lines[row];
            var formatted = NewText(content, TextBrush, TextFontSize, bodyWidth + RowWidthTolerance);

            context.DrawText(formatted, new Point(bodyX, lineY + LabelOffset + row * LineHeight));
        }

        context.DrawText(stamp, new Point(textX, lineY + LabelOffset));
    }

    private void DrawEmptyHint(DrawingContext context, double width, double height) =>
        TrackText.DrawHint(context, EmptyHint, Math.Max(TextInset, 0), width, height);

    // ------------------------------------------------------------ 换行

    private string[] Wrapped(string text, double width)
    {
        var key = (text, (int)Math.Round(width));
        if (_wrapCache.TryGetValue(key, out var cached)) return cached;

        var lines = WrapText(text, width);
        _wrapCache[key] = lines;
        return lines;
    }

    /// <summary>时间戳在整段文字左侧占去的留白宽度，于是正文每一行的可用宽度都是它。</summary>
    private static double BodyWidth(TimeSpan start, double available)
    {
        var stamp = NewText(LrcTimecode.Format(start), TimestampBrush, TextFontSize, Math.Min(available, TimestampMaxWidth));
        return available - stamp.Width - TimestampSeparation;
    }

    /// <summary>
    /// 把正文按宽度切成若干行。
    /// </summary>
    /// <remarks>
    /// 逐<b>字符</b>贪心，不按词切。歌词以中文为主，单词边界这个概念本来就不适用；
    /// 而贪心填满每一行，也正是「同样的行数里显示尽可能多的文字」——
    /// 若为了长短均衡而把行切得更平均，能显示的字反而更少。
    /// </remarks>
    private static string[] WrapText(string text, double width)
    {
        var lines = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var count = FitCharacters(text, start, width);

            // 窄到连一个字都放不下也必须往前走，否则这里就是个死循环。
            if (count <= 0) count = 1;

            lines.Add(text.Substring(start, count));
            start += count;
        }

        return lines.Count == 0 ? [text] : [.. lines];
    }

    /// <summary>从 <paramref name="start"/> 起，最多能放下多少个字符。</summary>
    /// <remarks>
    /// 宽度随字符数单调不减，所以直接二分，省掉「一个一个字试着加」的线性测量。
    /// </remarks>
    private static int FitCharacters(string text, int start, double width)
    {
        var low = 0;
        var high = text.Length - start;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (MeasureWidth(text.Substring(start, middle)) <= width) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    private static double MeasureWidth(string text) =>
        new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, TextFontSize, TextBrush)
        {
            MaxLineCount = 1,
        }.Width;

    private static double MeasureHeight(string text, double fontSize) =>
        new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, fontSize, TextBrush).Height;

    // ------------------------------------------------------------ 小工具

    /// <summary>
    /// 造一段带省略号截断的单行文字。
    /// </summary>
    /// <remarks>
    /// 实现在 <see cref="TrackText.Truncated"/> 一处，与未绑定歌词栏共用。这里转发一下，
    /// 只是为了让本文件里那些调用点读起来短一些；那两条不能省的规矩（
    /// <c>MaxTextWidth</c> 与 <c>MaxLineCount</c>）连同它们的来历都记在实现那一边。
    /// </remarks>
    private static FormattedText NewText(string text, IBrush brush, double fontSize, double maxWidth) =>
        TrackText.Truncated(text, brush, fontSize, maxWidth);

    private static IBrush BlockBrushFor(int count) =>
        BlockBrushes[Math.Clamp(count, 0, BlockBrushes.Length - 1)];

    private static IBrush ActiveBlockBrushFor(int count) =>
        ActiveBlockBrushes[Math.Clamp(count, 0, ActiveBlockBrushes.Length - 1)];

    private static IBrush BandBrushFor(int count) =>
        BandBrushes[Math.Clamp(count, 0, BandBrushes.Length - 1)];

    /// <summary>
    /// 色块的不透明度：行数越多越不透明，封顶在半透明附近。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是因为它还有一个用处：<b>决定块上的字该用深色还是浅色</b>。
    /// 底色越亮、字就该越深，而「亮不亮」正是这个不透明度说了算。
    /// </remarks>
    private static int BlockAlpha(int count) => Math.Clamp(0x30 + count * 0x08, 0x30, 0x88);

    /// <summary>
    /// 块内那条「这几条已选中」的灰带的不透明度：比同尺寸的整块底色再亮一档。
    /// </summary>
    /// <remarks>
    /// 为什么不直接用 <see cref="BlockAlpha"/>：灰带是铺在<b>粉色色块之上</b>的，
    /// 按同一档叠上去之后混出来仍是偏粉的浅色，只是"稍微亮一点的粉"，
    /// 读不出「这是一块灰」。抬高约 12 个百分点之后它才真的与底色分开。
    /// 上限仍然压在七成以下：这条带横跨两栏，压在波形上时不能把那一段音频盖掉。
    /// </remarks>
    private static int BandAlpha(int count) => Math.Clamp(BlockAlpha(count) + 0x20, 0x50, 0xA8);

    private static IBrush[] BuildBlockBrushes(Color color)
    {
        var brushes = new IBrush[17];

        for (var index = 0; index < brushes.Length; index++)
        {
            var alpha = (byte)BlockAlpha(index);
            brushes[index] = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        return brushes;
    }

    /// <summary>块内灰带的那一族刷子。它与色块刷子分开，只因为不透明度高一档，理由见 <see cref="BandAlpha"/>。</summary>
    private static IBrush[] BuildBandBrushes()
    {
        var brushes = new IBrush[17];

        for (var index = 0; index < brushes.Length; index++)
        {
            brushes[index] = new SolidColorBrush(Colors.White, BandAlpha(index) / 255.0);
        }

        return brushes;
    }
}
