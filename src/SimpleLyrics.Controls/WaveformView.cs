using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using SimpleLyrics.Core.Peaks;

namespace SimpleLyrics.Controls;

/// <summary>
/// 竖直时间轴的波形控件：<b>时间从上到下递增</b>，中心线位于控件正中，
/// 左声道向中心线左侧伸展、右声道向右侧伸展，形成背靠背的对称布局。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是竖直的</b>：本软件的编辑区是窄而高的侧栏，横向空间稀缺、纵向空间充裕，
/// 而打轴恰好需要看到尽可能长的连续时间跨度——把时间放到纵向是唯一合理的选择。
/// </para>
/// <para>
/// <b>精度取舍</b>：屏幕上的每一行内容是一条<b>波带</b>——固定由若干个峰值桶聚合而成，
/// 长度取该区间内的对称包络 <c>max(|min|, |max|)</c>。屏幕高度不过千余行，
/// 因此无论缩放到哪一级、音频有多长，每帧的绘制量都是常数级的：数据源是峰值金字塔
/// 而非原始采样，绘制量由波带数而非采样数决定。
/// </para>
/// <para>
/// <b>取数路径</b>：波形只经 <see cref="PeakPyramid.FillEnvelope"/> 取数，
/// 控件不接触 PCM，也不持有整曲数据。播放缓存是另一套独立的数据，与这里无关。
/// </para>
/// <para>
/// <b>播放指示器</b>：一条横线，位置由 <see cref="IndicatorRatio"/> 决定。
/// 播放时波形滚动、指示器不动，于是线下方恒为当前播放时刻；
/// 暂停时拖动这条线可以改变它的高度占比。
/// </para>
/// </remarks>
public sealed class WaveformView : Control
{
    /// <summary>波形与控件左右边缘之间保留的空白。</summary>
    private const double EdgePadding = 9;

    /// <summary>滚轮一格的滚动距离（像素）。</summary>
    private const double WheelPixelsPerNotch = 110;

    /// <summary>Ctrl + 滚轮每一格的缩放倍率。</summary>
    private const double ZoomStep = 1.25;

    private const double MinimumPixelsPerSecond = 0.5;
    private const double MaximumPixelsPerSecond = 4000;

    /// <summary>相邻主刻度之间至少保留的像素距离，决定刻度疏密。</summary>
    private const double MinimumTickGapPixels = 58;

    /// <summary>包络小于此值就不画，省掉大量无意义的零长度线段。</summary>
    private const float SilenceThreshold = 0.0015f;

    /// <summary>
    /// 一条波带的线宽（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 刻意画得粗，因为细线在这个场景下站不住：1 像素宽的线整条都是边缘，
    /// 在 125% / 150% 这类非整数缩放比下抗锯齿会把它摊到相邻两个物理像素上，
    /// 摊开比例随位置变化，看上去就是线条在粗与细之间来回跳。线宽到 6 像素后，
    /// 只有上下各一个物理像素参与过渡，偏差被摊薄到百分之一量级。
    /// </remarks>
    private const double BandWidth = 6;

    /// <summary>相邻两条波带之间留出的空隙（逻辑像素）。</summary>
    /// <remarks>
    /// 留空隙不只是为了好看：相邻的线若紧挨着，它们在物理像素上会互相「接力」——
    /// 上一行溢出到边界像素的那部分，被下一行补足，而两行颜色并不同，
    /// 于是那个像素的颜色会随滚动在两色之间摇摆。让每条带各占各的地盘，
    /// 这个混合就不存在了。
    /// </remarks>
    private const double BandGap = 2;

    /// <summary>一条波带的周期（逻辑像素）：线宽 + 空隙。缩放上限由它推出。</summary>
    private const double BandPitch = BandWidth + BandGap;

    /// <summary>指针离指示器多近才认定为「抓住了它」。</summary>
    private const double IndicatorHitTolerance = 7;

    /// <summary>
    /// 滚动的超限倍率：以整曲时长为单位，向两端各放宽这么多。
    /// </summary>
    /// <remarks>
    /// 指示器默认不在控件顶端，所以「把第一句歌词顶到指示器位置」需要滚动到 0 秒之前，
    /// 「把最后一句顶上去」需要滚动到时长的下界之后。放宽一个整曲长度对所有指示器位置都够用。
    /// </remarks>
    private const double OverscrollFactor = 1.0;

    // ------------------------------------------------------------------ 外观

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14));

    /// <summary>左声道：靠近中心亮、靠边缘淡。</summary>
    private static readonly IBrush LeftWave = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x4A, 0x38, 0xBD, 0xF8), 0),
            new GradientStop(Color.FromArgb(0xFF, 0x7D, 0xD3, 0xFC), 1),
        },
    };

    /// <summary>右声道：与左声道镜像，色调略偏紫以便一眼区分。</summary>
    private static readonly IBrush RightWave = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xFF, 0xB3, 0x9B, 0xFC), 0),
            new GradientStop(Color.FromArgb(0x4A, 0x8B, 0x5C, 0xF6), 1),
        },
    };

    private static readonly IPen CenterPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1);

    private static readonly IPen MajorTickPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);

    private static readonly IPen MinorTickPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), 1);

    private static readonly IBrush LabelBrush =
        new SolidColorBrush(Color.FromArgb(0xC0, 0xD8, 0xDF, 0xEC));

    private static readonly IBrush HintBrush =
        new SolidColorBrush(Color.FromArgb(0x78, 0xD8, 0xDF, 0xEC));

    /// <summary>音频起点 / 终点的界线与标注。用绿色系，与琥珀色的指示器一眼可分。</summary>
    private static readonly IBrush AudioBoundBrush =
        new SolidColorBrush(Color.FromRgb(0x3F, 0xD9, 0xA0));

    private static readonly IPen AudioBoundPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x3F, 0xD9, 0xA0)), 1.5);

    /// <summary>缓外区域的遮罩。压在波形之上，让未解码的部分整体暗下去。</summary>
    private static readonly IBrush CacheMaskBrush =
        new SolidColorBrush(Color.FromArgb(0x99, 0x08, 0x09, 0x0C));

    private static readonly IBrush IndicatorBrush =
        new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4A));

    /// <summary>指示器的外圈辉光，让它在密波形上也能被一眼找到。</summary>
    private static readonly IPen IndicatorGlowPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC2, 0x4A)), 5);

    private static readonly IPen IndicatorPen =
        new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC2, 0x4A)), 1.5);

    private static readonly IBrush TipBackBrush =
        new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x21, 0x2B));

    private static readonly IBrush TipTextBrush =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xEC, 0xF4));

    private static readonly Typeface LabelTypeface = Typeface.Default;

    private static readonly Cursor IndicatorCursor = new(StandardCursorType.SizeNorthSouth);

    /// <summary>悬停在波形内容上时的光标：四向箭头，提示「这块内容可以拖着走」。</summary>
    private static readonly Cursor ContentCursor = new(StandardCursorType.SizeAll);

    /// <summary>主刻度的候选步长（秒）。从毫秒级到两小时，覆盖任何可能的缩放。</summary>
    private static readonly double[] TickSteps =
    [
        0.01, 0.02, 0.05, 0.1, 0.2, 0.5,
        1, 2, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600, 7200,
    ];

    // ------------------------------------------------------------------ 状态

    /// <summary>按「波带」聚合出来的包络，<c>[声道][带号]</c>。</summary>
    private float[][]? _bands;

    /// <summary>每条波带包含多少个基础桶。</summary>
    private int _bandBuckets;

    /// <summary>整曲一共多少条波带。</summary>
    private int _bandCount;

    /// <summary>带数组对应的金字塔，以及当时每条带的桶数，两者一起构成缓存键。</summary>
    private PeakPyramid? _renderedPeaks;
    private int _renderedBandBuckets;

    /// <summary>载入新文件后需要按窗口高度重新适配一次；首次布局前拿不到正确高度。</summary>
    private bool _pendingAutoFit;

    private bool _draggingIndicator;

    /// <summary>正在按住拖动波形内容做精细定位。</summary>
    private bool _draggingWaveform;

    /// <summary>
    /// 拖动波形时指针抓住的那个时刻。
    /// </summary>
    /// <remarks>
    /// 存下这个不变量、每次用「它减去指针当前的时刻」重算滚动位置，而不是逐帧累加位移：
    /// 累加会把浮点误差攒起来，而且中途一旦撞上滚动边界，被截掉的那一段位移就永久丢了——
    /// 「往下拖 100 像素再拖回原处」将回不到出发点。用绝对基准，来回拖几次也精确复位。
    /// 存「时刻」而不是「顶端的时刻 + 按下点的纵坐标」，是因为拖动途中缩放万一变了，
    /// 前者依然有确切含义：指针底下那一点始终是同一个时刻。
    /// </remarks>
    private double _dragOriginSeconds;

    private bool _pointerInside;
    private double _pointerY;
    private string? _hoverText;

    // ------------------------------------------------------------ 依赖属性

    /// <summary>要显示的峰值金字塔。</summary>
    public static readonly StyledProperty<PeakPyramid?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, PeakPyramid?>(nameof(Peaks));

    /// <summary>纵向滚动位置（秒），即控件顶端对应的时间。可以为负，见 <see cref="OverscrollFactor"/>。</summary>
    public static readonly StyledProperty<double> ScrollSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(
            nameof(ScrollSeconds), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>纵向缩放，每秒钟占多少像素。取值会按数据精度与窗口高度自动钳制。</summary>
    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<WaveformView, double>(
            nameof(PixelsPerSecond), 40d,
            defaultBindingMode: BindingMode.TwoWay,
            coerce: CoercePixelsPerSecond);

    /// <summary>播放指示器距控件顶端的比例，0 为顶端、1 为底端。</summary>
    public static readonly StyledProperty<double> IndicatorRatioProperty =
        AvaloniaProperty.Register<WaveformView, double>(
            nameof(IndicatorRatio), 0.4,
            defaultBindingMode: BindingMode.TwoWay,
            coerce: CoerceRatio);

    /// <summary>
    /// 当前播放位置（秒）。<see cref="double.NaN"/> 表示没有播放位置，此时控件不跟随。
    /// </summary>
    /// <remarks>
    /// 播放中它被外部持续更新，控件据此滚动波形，使指示器所在处恒为该时刻。
    /// 反过来说，用户滚动或拖动指示器时，控件会写回这个值——那正是「用户想要跳到别处」的表达。
    /// </remarks>
    public static readonly StyledProperty<double> PlayheadSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(
            nameof(PlayheadSeconds), double.NaN,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>是否正在播放。影响跟随行为与滚轮语义。</summary>
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(IsPlaying));

    /// <summary>已解码缓存区间的起始时刻（秒）。</summary>
    public static readonly StyledProperty<double> CacheStartSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(CacheStartSeconds), double.NaN);

    /// <summary>已解码缓存区间的结束时刻（秒）。</summary>
    public static readonly StyledProperty<double> CacheEndSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(CacheEndSeconds), double.NaN);

    /// <summary>指示器当前所指的时刻（秒）。只读，供外部得知用户把播放头放到了哪里。</summary>
    public static readonly StyledProperty<double> PlayStartSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(
            nameof(PlayStartSeconds), defaultBindingMode: BindingMode.OneWayToSource);

    static WaveformView()
    {
        AffectsRender<WaveformView>(
            PeaksProperty,
            ScrollSecondsProperty,
            PixelsPerSecondProperty,
            IndicatorRatioProperty,
            PlayheadSecondsProperty,
            IsPlayingProperty,
            CacheStartSecondsProperty,
            CacheEndSecondsProperty);

        PeaksProperty.Changed.AddClassHandler<WaveformView>(static (view, _) => view.OnPeaksChanged());
    }

    public PeakPyramid? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
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

    public double IndicatorRatio
    {
        get => GetValue(IndicatorRatioProperty);
        set => SetValue(IndicatorRatioProperty, value);
    }

    public double PlayheadSeconds
    {
        get => GetValue(PlayheadSecondsProperty);
        set => SetValue(PlayheadSecondsProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double CacheStartSeconds
    {
        get => GetValue(CacheStartSecondsProperty);
        set => SetValue(CacheStartSecondsProperty, value);
    }

    public double CacheEndSeconds
    {
        get => GetValue(CacheEndSecondsProperty);
        set => SetValue(CacheEndSecondsProperty, value);
    }

    public double PlayStartSeconds
    {
        get => GetValue(PlayStartSecondsProperty);
        set => SetValue(PlayStartSecondsProperty, value);
    }

    /// <summary>当前视口覆盖的秒数。</summary>
    public double ViewportSeconds
    {
        get
        {
            var height = Bounds.Height;
            return height <= 1 ? 0 : height / Math.Max(PixelsPerSecond, 1e-6);
        }
    }

    /// <summary>
    /// 当前素材下缩放的实际上限（像素/秒）。
    /// </summary>
    /// <remarks>
    /// 公开出来是给诊断工具用的：它要挑「最坏情况」的档位，就得与控件用同一个判据。
    /// 工具自己再推一遍（比如写成「一个桶占一个像素」）会随着绘制方案的调整悄悄脱节，
    /// 那时测出来的「最放大」根本不是控件允许的最放大。
    /// </remarks>
    public double MaximumZoom =>
        Peaks is { DurationSeconds: > 0 } peaks ? MaximumFor(peaks) : MaximumPixelsPerSecond;

    // ------------------------------------------------------------ 公共操作

    /// <summary>把整曲缩放到刚好铺满窗口高度，并让曲首对齐到指示器。</summary>
    /// <remarks>
    /// <para>
    /// 高度还没算出来时只是记下待办，等第一次布局完成后由 <see cref="Render"/> 补做。
    /// </para>
    /// <para>
    /// <b>播放中必须连播放位置一起搬。</b>只改视图是不够的：适配把曲首摆到指示器上，
    /// 但紧接着（最多 8 毫秒后）位置刷新会触发 <see cref="FollowPlayhead"/>，
    /// 按新缩放把波形重新对齐到<b>当前播放时刻</b>——缩放确实复位了，位置却纹丝不动，
    /// 看上去就是「复位按钮无效，还在原地往下播」。把播放头一并挪到指示器所指的时刻，
    /// 跟随逻辑算出来的滚动位置与适配结果一致，两者不再互相拆台。
    /// </para>
    /// </remarks>
    public void FitToWindow()
    {
        var peaks = Peaks;
        var height = Bounds.Height;

        if (peaks is null || height <= 1 || peaks.DurationSeconds <= 0)
        {
            _pendingAutoFit = true;
            return;
        }

        _pendingAutoFit = false;
        ApplyFit(peaks, height);

        // 适配之后指示器指的正是曲首，这个赋值把播放位置也拉到那里。
        if (IsPlaying) SetCurrentValue(PlayheadSecondsProperty, PlayStartSeconds);
    }

    /// <summary>
    /// 以指示条为锚点把缩放乘以 <paramref name="factor"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 锚点固定是<b>指示条</b>：缩放前后它所指的时刻不变，播放位置因此也不变。
    /// 曾经以鼠标指针为锚，理由是「视线不丢」。但那与本控件的基本约定相冲——
    /// 指示条下方恒为当前时刻。指针在别处时，缩放先围绕指针把内容摆好，
    /// 紧接着位置刷新又按指示条重新对齐一次，中间那一帧便被看见：
    /// 波形像是先以鼠标为中心缩放，然后猛地瞬移到指示条上。锚在指示条上，
    /// 两次计算给出的是同一个结果，瞬移无从发生。
    /// </para>
    /// <para>
    /// 缩放语义只此一处入口，滚轮与将来可能有的「放大 / 缩小」按钮都走它，
    /// 免得两处各自的锚点慢慢分家。
    /// </para>
    /// </remarks>
    public void ZoomBy(double factor)
    {
        var peaks = Peaks;
        var height = Bounds.Height;

        if (peaks is null || height <= 1 || peaks.DurationSeconds <= 0) return;
        if (!double.IsFinite(factor) || factor <= 0) return;

        var pixelsPerSecond = Math.Max(PixelsPerSecond, 1e-6);
        var anchorY = Math.Clamp(IndicatorRatio, 0, 1) * height;
        var anchorSeconds = ScrollSeconds + anchorY / pixelsPerSecond;

        // 赋值会经过 coerce 钳制，下面读回的已是钳制后的值，锚点偏移因此自然正确。
        PixelsPerSecond = pixelsPerSecond * factor;

        ScrollSeconds = anchorSeconds - anchorY / Math.Max(PixelsPerSecond, 1e-6);
        ClampScroll();

        // 钳制有可能挪动滚动位置，那样指示条指的就不再是原来那一刻，需要同步给播放器。
        if (IsPlaying) SetCurrentValue(PlayheadSecondsProperty, PlayStartSeconds);

        UpdatePlayStartSeconds();
        InvalidateVisual();
    }

    /// <summary>
    /// 开始一次「拖动波形定位」：记下基准，之后的位移都相对它计算。
    /// </summary>
    /// <remarks>
    /// 与拖动指示条分工不同。<b>拖指示条是让指示条在窗口里换位置、内容不动</b>，
    /// 回答的是「我希望播放头落在窗口的哪个高度」；<b>拖波形是让内容跟着手走</b>，
    /// 回答的是「我想听的那一小段在哪儿」。两者都改播放位置，但手感完全不同，
    /// 因此按指针是否落在指示条附近分开处理，那条分界线是看得见的光标形状。
    /// </remarks>
    public void BeginWaveformDrag(double y)
    {
        if (Peaks is not { DurationSeconds: > 0 } || Bounds.Height <= 1) return;

        _draggingWaveform = true;
        _dragOriginSeconds = ScrollSeconds + y / Math.Max(PixelsPerSecond, 1e-6);

        // 拖动中指针可能已经跑出波形范围，留在原地的悬浮提示会误导。
        _hoverText = null;
    }

    /// <summary>
    /// 按指针位移滚动内容：指针下移多少像素，内容就下移多少像素。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 跟随比例固定 1:1，也就是 1 像素 = <c>1 ÷ 每秒像素</c> 秒——每屏 18 秒时约 24 毫秒，
    /// 最大放大时不到 1 毫秒。「精细」是靠放大换来的；拖动本身不做减速，
    /// 否则慢拖与快拖会给出不同的位移，手感变得不可预测。
    /// </para>
    /// <para>
    /// <b>拖动过程中绝不把位置推给播放器</b>，只有 <see cref="EndWaveformDrag"/> 才提交一次。
    /// 理由见那里。
    /// </para>
    /// </remarks>
    public void UpdateWaveformDrag(double y)
    {
        if (!_draggingWaveform) return;

        // 指针抓住的那一点始终对应同一个时刻：指针下移 = 内容下移 = 顶端对着更早的时刻。
        ScrollSeconds = _dragOriginSeconds - y / Math.Max(PixelsPerSecond, 1e-6);
        ClampScroll();
        UpdatePlayStartSeconds();

        InvalidateVisual();
    }

    /// <summary>
    /// 结束拖动，把最终位置提交出去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么松手才提交。</b>提交一次就要走一遍 <c>AudioPlayer.Play</c>，而它为了清掉
    /// 设备缓冲里残留的旧内容必须先 <c>Stop</c>（即 <c>waveOutReset</c>）。鼠标一秒钟能移动
    /// 几十次，每次都 reset 一遍，声音会碎成一串咔嗒。所以拖动中只动视图、松手才落定；
    /// 代价是这段时间耳朵里听到的仍是原位置的声音，屏幕上另给一行提示说明这件事。
    /// </para>
    /// <para>
    /// <b>未播放时不需要这个提交。</b>那时位置只能从 <see cref="PlayStartSeconds"/> 出去，
    /// 而它在拖动过程中已经逐帧更新，视图模型的延迟准备被反复重置、松手后自然接手。
    /// 播放中恰恰相反：滚动位置只是中间产物（<c>IsPlaying</c> 时视图模型刻意忽略它），
    /// 不推一把播放头就没有任何东西会动。
    /// </para>
    /// </remarks>
    public void EndWaveformDrag()
    {
        if (!_draggingWaveform) return;

        _draggingWaveform = false;

        if (IsPlaying) SetCurrentValue(PlayheadSecondsProperty, PlayStartSeconds);

        UpdateHover();
        InvalidateVisual();
    }

    // ------------------------------------------------------------ 属性联动

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        var property = change.Property;

        if (property == PlayheadSecondsProperty)
        {
            FollowPlayhead();
            UpdatePlayStartSeconds();
        }
        else if (property == IndicatorRatioProperty)
        {
            // 指示器一动，它所指的时刻就变了；播放中要跟着重新对齐滚动。
            FollowPlayhead();
            UpdatePlayStartSeconds();
        }
        else if (property == ScrollSecondsProperty || property == PixelsPerSecondProperty)
        {
            UpdatePlayStartSeconds();
        }
        else if (property == BoundsProperty)
        {
            UpdatePlayStartSeconds();
        }
        else if (property == IsPlayingProperty)
        {
            UpdateHover();
        }
    }

    /// <summary>
    /// 让指示器所在处对齐到 <see cref="PlayheadSeconds"/>。
    /// </summary>
    /// <remarks>
    /// 拖动过程中刻意不跟随：用户此刻在重新摆放指示器，内容应当原地不动，
    /// 否则「设置指示器相对于窗口的位置」这个动作会变成「滚动内容」，与直觉相反。
    /// </remarks>
    private void FollowPlayhead()
    {
        // 拖动期间一律不跟随：用户此刻正在亲手摆放内容或指示条，
        // 位置刷新若把手里的东西抢回去，拖动就变成了和播放器拔河。
        if (_draggingIndicator || _draggingWaveform) return;
        if (!double.IsFinite(PlayheadSeconds)) return;

        var height = Bounds.Height;
        if (height <= 1) return;

        var viewport = height / Math.Max(PixelsPerSecond, 1e-6);

        ScrollSeconds = PlayheadSeconds - IndicatorRatio * viewport;
        ClampScroll();
    }

    private void UpdatePlayStartSeconds()
    {
        SetValue(PlayStartSecondsProperty, ScrollSeconds + IndicatorRatio * ViewportSeconds);
    }

    private void OnPeaksChanged()
    {
        _renderedPeaks = null;
        _hoverText = null;

        if (Peaks is null)
        {
            _pendingAutoFit = false;
            InvalidateVisual();
            return;
        }

        _pendingAutoFit = true;
        TryAutoFit();
        InvalidateVisual();
    }

    /// <summary>载入新文件后把整曲缩放到刚好铺满窗口高度。</summary>
    private void TryAutoFit()
    {
        if (!_pendingAutoFit) return;

        var peaks = Peaks;
        var height = Bounds.Height;
        if (peaks is null || height <= 1 || peaks.DurationSeconds <= 0) return;

        _pendingAutoFit = false;
        ApplyFit(peaks, height);
    }

    /// <summary>
    /// 把整曲适配到窗口高度，并让曲首落在指示器所在处。
    /// </summary>
    /// <remarks>
    /// 之所以不是简单地把顶端时刻归零：指示器通常在窗口偏上处，若顶端就是 0 秒，
    /// 播放头指的便是曲子靠中间的位置，按下播放会从那里开始，与「从头听起」的预期不符。
    /// 让 0 秒落在指示器上，屏幕上方那一段空白正好表达「这里是开头，前面没有内容」。
    /// </remarks>
    private void ApplyFit(PeakPyramid peaks, double height)
    {
        PixelsPerSecond = height / peaks.DurationSeconds;
        ScrollSeconds = -IndicatorRatio * ViewportSeconds;
        ClampScroll();
        _renderedPeaks = null;
    }

    /// <summary>
    /// 缩放值的强制范围。放在属性系统里而不是各个调用点，是为了让<b>任何</b>赋值路径
    /// （滚轮、数据绑定、复位按钮、将来可能有的「缩放到选区」）都受同一约束。
    /// </summary>
    private static double CoercePixelsPerSecond(AvaloniaObject instance, double value)
    {
        if (instance is not WaveformView view || !double.IsFinite(value)) return value;

        var peaks = view.Peaks;
        if (peaks is null || peaks.DurationSeconds <= 0) return value;

        return Math.Clamp(value, MinimumFor(peaks, view.Bounds.Height), MaximumFor(peaks));
    }

    private static double CoerceRatio(AvaloniaObject instance, double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.4;

    /// <summary>不允许缩到比「整曲铺满窗口」还小，否则下方会留下永远滚不到的空白。</summary>
    private static double MinimumFor(PeakPyramid peaks, double height) =>
        Math.Clamp(height / Math.Max(peaks.DurationSeconds, 0.001), MinimumPixelsPerSecond, MaximumPixelsPerSecond);

    /// <summary>
    /// 最大缩放：让金字塔最细的一个桶刚好铺满一条波带。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上限由数据精度决定，而不是由手感决定：再放大，一条带就装不下一个桶，
    /// 带上的值是同一个桶被摊到更宽的面积上，看着像台阶而不是细节，
    /// 反而误导对音头位置的判断。
    /// </para>
    /// <para>
    /// 判据从「桶占满一个像素」放宽到「桶占满一条带」，是因为波形改按带绘制之后，
    /// 一个桶只会画成<b>一条</b>线，不会出现同一个桶被拉成若干条等高横线的情形。
    /// 对 44.1 kHz 素材上限约 1379 像素/秒，此时一条带的周期正好等于一个桶（5.8 毫秒）。
    /// 线宽变细会同步压低这个上限——上限由 <see cref="BandPitch"/> 推出，而
    /// <see cref="BandPitch"/> 就是线宽加空隙。
    /// </para>
    /// </remarks>
    private static double MaximumFor(PeakPyramid peaks)
    {
        var bucketSeconds = peaks.BaseLevel.BucketSeconds(peaks.SampleRate);
        var fromData = bucketSeconds > 0 ? BandPitch / bucketSeconds : MaximumPixelsPerSecond;
        return Math.Clamp(fromData, MinimumPixelsPerSecond, MaximumPixelsPerSecond);
    }

    /// <summary>
    /// 把滚动位置钳制在「音频两端各放宽一个整曲长度」的范围内。
    /// </summary>
    /// <remarks>
    /// 之所以允许越界，是因为指示器通常不在窗口顶端：要把 0 秒对齐到指示器，
    /// 顶端时刻就必须是负数。音频之外的那段会画成纯背景（不标「未解码」），
    /// 因为那里本来就没有内容，标成缓存缺失是误导。
    /// </remarks>
    private void ClampScroll()
    {
        var peaks = Peaks;
        var height = Bounds.Height;

        if (peaks is null || height <= 0 || peaks.DurationSeconds <= 0)
        {
            ScrollSeconds = 0;
            return;
        }

        var duration = peaks.DurationSeconds;
        var viewport = height / Math.Max(PixelsPerSecond, 1e-6);
        var overscroll = duration * OverscrollFactor;

        ScrollSeconds = Math.Clamp(ScrollSeconds, -overscroll, duration - viewport + overscroll);
    }

    // ------------------------------------------------------------ 交互

    /// <summary>滚轮上下滚动；Ctrl + 滚轮缩放，一律以指示条为锚点。</summary>
    /// <remarks>
    /// 播放中滚动的语义不同：指示器下方恒为当前播放时刻，所以滚动多少就等于把播放位置
    /// 移动多少。这里把结果写回 <see cref="PlayheadSeconds"/>，由外部决定何时真正跳转。
    /// </remarks>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var peaks = Peaks;
        var height = Bounds.Height;

        if (peaks is null || height <= 1 || peaks.DurationSeconds <= 0 || Math.Abs(e.Delta.Y) < 1e-6)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        var pixelsPerSecond = Math.Max(PixelsPerSecond, 1e-6);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomBy(Math.Pow(ZoomStep, e.Delta.Y));
            e.Handled = true;
            return;
        }

        // 向上滚 = 内容上移 = 看到更早的时间。
        ScrollSeconds -= e.Delta.Y * WheelPixelsPerNotch / pixelsPerSecond;
        ClampScroll();

        if (IsPlaying) SetCurrentValue(PlayheadSecondsProperty, PlayStartSeconds);

        UpdatePlayStartSeconds();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// 左键按下：靠近指示条就拖指示条，落在别处就拖内容。
    /// </summary>
    /// <remarks>
    /// 分界线就是光标形状——悬停时鼠标指针已经预告了会拖到什么。指示条优先，
    /// 因为那条带很窄（<see cref="IndicatorHitTolerance"/> 像素），
    /// 被内容拖动抢走的话就再也抓不住了。
    /// </remarks>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsLeftButtonPressed)
        {
            var y = e.GetPosition(this).Y;

            if (IsNearIndicator(y))
            {
                _draggingIndicator = true;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (Peaks is { DurationSeconds: > 0 } && Bounds.Height > 1)
            {
                BeginWaveformDrag(y);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        _pointerY = point.Y;
        _pointerInside = true;

        if (_draggingIndicator)
        {
            var height = Bounds.Height;
            if (height > 1) SetCurrentValue(IndicatorRatioProperty, Math.Clamp(point.Y / height, 0, 1));

            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (_draggingWaveform)
        {
            UpdateWaveformDrag(point.Y);
            e.Handled = true;
            return;
        }

        UpdateHover();
        Cursor = IsNearIndicator(point.Y) ? IndicatorCursor : ContentCursor;

        // 提示框跟着指针走，所以只要指针在动就得重画。包络有缓存，这里只重绘线段。
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingIndicator)
        {
            _draggingIndicator = false;
            e.Pointer.Capture(null);

            // 松手才提交：拖动过程中每移动一像素就跳一次会让声音碎成一串。
            if (IsPlaying) SetCurrentValue(PlayheadSecondsProperty, PlayStartSeconds);

            UpdateHover();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_draggingWaveform)
        {
            EndWaveformDrag();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    /// <summary>
    /// 捕获被别处抢走时的收尾。
    /// </summary>
    /// <remarks>
    /// 拖动途中窗口失活（Alt+Tab、弹出对话框）就拿不到 <c>PointerReleased</c> 了。
    /// 不在这里清掉标志的话它会一直立着，此后位置刷新永远被跳过——
    /// 表现是「播放时波形再也不跟着滚了」，而用户完全不知道自己做错了什么。
    /// </remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_draggingIndicator || _draggingWaveform)
        {
            _draggingIndicator = false;
            _draggingWaveform = false;
            InvalidateVisual();
        }

        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointerInside = false;
        _hoverText = null;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    private bool IsNearIndicator(double y) =>
        Math.Abs(y - Math.Clamp(IndicatorRatio, 0, 1) * Bounds.Height) <= IndicatorHitTolerance;

    /// <summary>
    /// 判断指针是否落在「音频之内、缓存之外」的区域。
    /// </summary>
    /// <remarks>
    /// 超限滚动出来的那段（0 秒之前、时长之后）刻意不提示：那里根本没有音频，
    /// 说它「尚未解码」会让人以为等一等就有了。
    /// </remarks>
    private void UpdateHover()
    {
        string? text = null;

        var peaks = Peaks;

        if (_pointerInside
            && peaks is not null
            && Bounds.Height > 1
            && double.IsFinite(CacheStartSeconds)
            && double.IsFinite(CacheEndSeconds))
        {
            var seconds = ScrollSeconds + _pointerY / Math.Max(PixelsPerSecond, 1e-6);

            if (seconds >= 0
                && seconds < peaks.DurationSeconds
                && (seconds < CacheStartSeconds || seconds >= CacheEndSeconds))
            {
                text = "此处尚未解码 —— 播放前需要先准备";
            }
        }

        _hoverText = text;
    }

    // ------------------------------------------------------------ 绘制

    public override void Render(DrawingContext context)
    {
        TryAutoFit();

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        // 波带是按「整条带」画出来的矩形，窗口最上、最下那两条必然有一部分落在控件之外——
        // 那正是「这条带只露出半截」应有的样子，不该被省掉。但控件默认不裁剪
        // （ClipToBounds 为 false），不夹住的话这几像素会直接画到相邻的工具栏和状态栏上，
        // 看起来就像线条跑出了波形区。指示器的辉光贴到上下边缘时同理。
        using (context.PushClip(new Rect(0, 0, width, height)))
        {
            RenderContent(context, width, height);
        }
    }

    private void RenderContent(DrawingContext context, double width, double height)
    {
        context.FillRectangle(Backdrop, new Rect(0, 0, width, height));

        var peaks = Peaks;
        if (peaks is null || peaks.BaseLevel.Count == 0)
        {
            DrawHint(context, width, height);
            return;
        }

        var pixelsPerSecond = Math.Max(PixelsPerSecond, 1e-6);
        FillBands(peaks, pixelsPerSecond);

        var center = width / 2;
        var reach = Math.Max(4, center - EdgePadding);

        DrawGridLines(context, width, height);

        // 中心线先画，让波形叠在上面，避免被线段切断视觉连续性。
        context.DrawLine(CenterPen, new Point(center, 0), new Point(center, height));

        var bands = _bands!;
        var bandSeconds = _bandBuckets * peaks.BaseLevel.BucketSeconds(peaks.SampleRate);
        var bandPixels = bandSeconds * pixelsPerSecond;

        // 画出来的厚度 = 周期减去空隙。至少留 1 像素，免得空隙把线整个吃掉。
        var thickness = Math.Max(1, bandPixels - BandGap);

        var scroll = ScrollSeconds;
        var firstBand = Math.Max(0, (int)Math.Floor(scroll / bandSeconds));
        var lastBand = Math.Min(
            _bandCount - 1,
            (int)Math.Ceiling((scroll + height / pixelsPerSecond) / bandSeconds));

        var drawRightSeparately = peaks.Channels > 1;

        for (var band = firstBand; band <= lastBand; band++)
        {
            var y = (band * bandSeconds - scroll) * pixelsPerSecond;
            if (y > height) break;

            var left = bands[0][band];
            if (left > SilenceThreshold)
            {
                var length = left * reach;
                context.FillRectangle(LeftWave, new Rect(center - length, y, length, thickness));
            }

            // 单声道素材在两侧镜像显示：既保持对称，也避免右半边空着显得像出错了。
            var right = drawRightSeparately ? bands[1][band] : left;
            if (right > SilenceThreshold)
            {
                var length = right * reach;
                context.FillRectangle(RightWave, new Rect(center, y, length, thickness));
            }
        }

        // 遮罩压在波形之上：缓外的那几段自动整体变暗，不必单独换一套颜色。
        DrawCacheMask(context, width, height);

        // 边界线画在遮罩之后：它标的是「音频到此为止」，被缓存遮罩压暗就失去意义了。
        DrawAudioBounds(context, width, height);

        DrawIndicator(context, width, height);

        // 标签最后画：充分放大时波形会一直伸到控件边缘，压在标签下面就完全读不出来了。
        DrawTickLabels(context, height);
        DrawHoverTip(context, height);

        // 拖动提示压在所有内容之上：它是「此刻正在发生什么」的说明，被盖住就没有意义。
        DrawDragHint(context, width, height);
    }

    /// <summary>
    /// 播放中拖动定位时的一行提示。
    /// </summary>
    /// <remarks>
    /// 播放中拖动刻意不实时跳转（每移动一次就 seek，声音会碎成一串），
    /// 于是这段时间耳朵听到的仍是原位置。不点明这一点，用户会以为拖动没生效，
    /// 或者以为声音已经跟着走了。未播放时不需要这行字——那时没有第二个「位置」存在，
    /// 波形跟手移动本身就说明了一切。
    /// </remarks>
    private void DrawDragHint(DrawingContext context, double width, double height)
    {
        if (!_draggingWaveform || !IsPlaying) return;

        var text = new FormattedText(
            "拖动定位中 —— 松开后从此处继续",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            11.5,
            TipTextBrush);

        var rect = new Rect(6, 6, Math.Min(text.Width + 14, width - 12), text.Height + 10);

        context.FillRectangle(TipBackBrush, rect, 4);
        context.DrawText(text, new Point(13, 11));
    }

    /// <summary>
    /// 在音频的起点与终点各画一条界线并标注。
    /// </summary>
    /// <remarks>
    /// 波形本身回答不了「音频到哪儿为止」：静音段（包络低于阈值不画）与音频之外
    /// 都是一片空背景，而本控件允许向两端超限滚动一个整曲长度，两者很容易混淆。
    /// 这两条线把有效区间圈出来，滚过头时也能一眼看出自己已经离开了音频。
    /// </remarks>
    private void DrawAudioBounds(DrawingContext context, double width, double height)
    {
        var peaks = Peaks;
        if (peaks is null || peaks.DurationSeconds <= 0) return;

        DrawAudioBound(context, width, height, 0, "音频开始");
        DrawAudioBound(context, width, height, peaks.DurationSeconds, "音频结束");
    }

    private void DrawAudioBound(
        DrawingContext context,
        double width,
        double height,
        double seconds,
        string caption)
    {
        var y = (seconds - ScrollSeconds) * Math.Max(PixelsPerSecond, 1e-6);
        if (y < 0 || y > height) return;

        context.DrawLine(AudioBoundPen, new Point(0, y), new Point(width, y));

        var text = new FormattedText(
            caption,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            10.5,
            AudioBoundBrush);

        // 贴在线的下沿靠右放；线太靠底时翻到上沿，免得被窗口裁掉。
        var textY = y + 3;
        if (textY + text.Height > height - 1) textY = y - text.Height - 3;

        context.DrawText(text, new Point(Math.Max(4, width - text.Width - 7), textY));
    }

    /// <summary>
    /// 按「波带」聚合整曲的包络：每条带包含固定数量的基础桶，画成一条粗横线。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要按带聚合</b>：屏幕行的时间边界由缩放决定，与金字塔的桶边界无关。
    /// 一行若只覆盖某个桶的一部分，取数时就把窗口向外扩到桶边界，读到的内容比名义窗口
    /// 更宽，而这个外扩量随滚动离散跳变，看起来就是波形在抖。改成「一条带 = 整数个桶」
    /// 之后，聚合边界与桶边界完全重合，外扩为零。
    /// </para>
    /// <para>
    /// <b>顺带得到的一个性质</b>：带的分组锚定在音频起点，每条带的值因此<b>与滚动位置无关</b>。
    /// 播放时只是整幅平移，内容恒定；这张表也只需在缩放变化时重算一次
    /// （总读取量恒为「基础桶数 × 声道数」，与缩放级别无关）。
    /// </para>
    /// <para>
    /// 代价是时间分辨率：一条带覆盖 <c>BandPitch / 每秒像素数</c> 秒，
    /// 带内部的时间细节被压成一个峰值。
    /// </para>
    /// </remarks>
    private void FillBands(PeakPyramid peaks, double pixelsPerSecond)
    {
        var level = peaks.BaseLevel;
        var bucketSeconds = level.BucketSeconds(peaks.SampleRate);
        var bucketPixels = bucketSeconds * pixelsPerSecond;

        // 向上取整而不是四舍五入：宁可让带略厚，也不要薄到抗锯齿重新占主导。
        var bucketsPerBand = Math.Max(1, (int)Math.Ceiling(BandPitch / Math.Max(bucketPixels, 1e-9)));

        if (ReferenceEquals(_renderedPeaks, peaks) && _renderedBandBuckets == bucketsPerBand) return;

        var bandCount = (level.Count + bucketsPerBand - 1) / bucketsPerBand;
        var channels = peaks.Channels;

        if (_bands is null || _bands.Length != channels || _bands[0].Length != bandCount)
        {
            _bands = new float[channels][];
            for (var channel = 0; channel < channels; channel++) _bands[channel] = new float[bandCount];
        }

        for (var band = 0; band < bandCount; band++)
        {
            var first = band * bucketsPerBand;
            var last = Math.Min(first + bucketsPerBand, level.Count);

            for (var channel = 0; channel < channels; channel++)
            {
                var min = float.MaxValue;
                var max = float.MinValue;

                for (var bucket = first; bucket < last; bucket++)
                {
                    var index = bucket * channels + channel;
                    if (level.Mins[index] < min) min = level.Mins[index];
                    if (level.Maxs[index] > max) max = level.Maxs[index];
                }

                var value = MathF.Max(MathF.Abs(min), MathF.Abs(max));
                _bands[channel][band] = value > 1f ? 1f : value;
            }
        }

        _bandBuckets = bucketsPerBand;
        _bandCount = bandCount;
        _renderedPeaks = peaks;
        _renderedBandBuckets = bucketsPerBand;
    }

    /// <summary>当前滚动与缩放下的刻度布局。</summary>
    private (double PixelsPerSecond, double Scroll, double EndSeconds, double MajorStep, double MinorStep) TickLayout(double height)
    {
        var pixelsPerSecond = Math.Max(PixelsPerSecond, 1e-6);
        var scroll = ScrollSeconds;
        var majorStep = ChooseTickStep(pixelsPerSecond);

        return (pixelsPerSecond, scroll, scroll + height / pixelsPerSecond, majorStep, majorStep / 5);
    }

    /// <summary>画水平的时间刻度线：主刻度之间补四条次级刻度。</summary>
    private void DrawGridLines(DrawingContext context, double width, double height)
    {
        var (_, scroll, endSeconds, majorStep, minorStep) = TickLayout(height);

        // 用整数序号乘步长而不是累加，避免长时间滚动后浮点误差累积。
        for (var index = (long)Math.Ceiling(scroll / minorStep); index <= (long)Math.Floor(endSeconds / minorStep); index++)
        {
            var y = (index * minorStep - scroll) * PixelsPerSecond;
            if (y < 0 || y > height) continue;
            context.DrawLine(MinorTickPen, new Point(0, y), new Point(width, y));
        }

        for (var index = (long)Math.Ceiling(scroll / majorStep); index <= (long)Math.Floor(endSeconds / majorStep); index++)
        {
            var y = (index * majorStep - scroll) * PixelsPerSecond;
            if (y < 0 || y > height) continue;
            context.DrawLine(MajorTickPen, new Point(0, y), new Point(width, y));
        }
    }

    /// <summary>画主刻度的时间标签。</summary>
    private void DrawTickLabels(DrawingContext context, double height)
    {
        var (pixelsPerSecond, scroll, endSeconds, majorStep, _) = TickLayout(height);

        for (var index = (long)Math.Ceiling(scroll / majorStep); index <= (long)Math.Floor(endSeconds / majorStep); index++)
        {
            var seconds = index * majorStep;
            var y = (seconds - scroll) * pixelsPerSecond;
            if (y < 0 || y > height) continue;

            var label = new FormattedText(
                FormatTimestamp(seconds, majorStep),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                10.5,
                LabelBrush);

            context.DrawText(label, new Point(6, y + 3));
        }
    }

    /// <summary>把「音频之内、缓存之外」的两段压暗。</summary>
    private void DrawCacheMask(DrawingContext context, double width, double height)
    {
        var peaks = Peaks;
        if (peaks is null) return;
        if (!double.IsFinite(CacheStartSeconds) || !double.IsFinite(CacheEndSeconds)) return;

        var duration = peaks.DurationSeconds;
        if (duration <= 0) return;

        DrawMaskSegment(context, width, height, 0, Math.Min(CacheStartSeconds, duration));
        DrawMaskSegment(context, width, height, Math.Max(CacheEndSeconds, 0), duration);
    }

    private void DrawMaskSegment(DrawingContext context, double width, double height, double fromSeconds, double toSeconds)
    {
        if (toSeconds <= fromSeconds) return;

        var pixelsPerSecond = Math.Max(PixelsPerSecond, 1e-6);
        var top = Math.Max((fromSeconds - ScrollSeconds) * pixelsPerSecond, 0);
        var bottom = Math.Min((toSeconds - ScrollSeconds) * pixelsPerSecond, height);
        if (bottom <= top) return;

        context.FillRectangle(CacheMaskBrush, new Rect(0, top, width, bottom - top));
    }

    /// <summary>画播放指示器：一条亮线加一个左侧尖角，以及它所指的时刻。</summary>
    private void DrawIndicator(DrawingContext context, double width, double height)
    {
        var y = Math.Clamp(IndicatorRatio, 0, 1) * height;

        context.DrawLine(IndicatorGlowPen, new Point(0, y), new Point(width, y));
        context.DrawLine(IndicatorPen, new Point(0, y), new Point(width, y));

        var marker = new StreamGeometry();
        using (var geometry = marker.Open())
        {
            geometry.BeginFigure(new Point(0, y - 5.5), true);
            geometry.LineTo(new Point(10, y));
            geometry.LineTo(new Point(0, y + 5.5));
            geometry.EndFigure(true);
        }

        context.DrawGeometry(IndicatorBrush, null, marker);

        var seconds = ScrollSeconds + Math.Clamp(IndicatorRatio, 0, 1) * ViewportSeconds;

        var text = new FormattedText(
            FormatIndicatorTimestamp(seconds),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            11,
            IndicatorBrush);

        // 贴在指示器下沿右侧；靠近底边时翻到上沿，避免被窗口裁掉。
        var textY = y + 4;
        if (textY + text.Height > height - 2) textY = y - text.Height - 4;

        context.DrawText(text, new Point(Math.Max(12, width - text.Width - 8), textY));
    }

    private void DrawHoverTip(DrawingContext context, double height)
    {
        if (!_pointerInside || _hoverText is null) return;

        var text = new FormattedText(
            _hoverText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            11.5,
            TipTextBrush);

        var y = Math.Clamp(_pointerY + 14, 4, Math.Max(4, height - text.Height - 10));
        var rect = new Rect(2, y - 5, text.Width + 14, text.Height + 10);

        context.FillRectangle(TipBackBrush, rect, 4);
        context.DrawText(text, new Point(9, y));
    }

    private void DrawHint(DrawingContext context, double width, double height)
    {
        var text = new FormattedText(
            "未载入音频\n\n点击「打开音频…」\n或按 Ctrl+O",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            12.5,
            HintBrush)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(40, width - 24),
        };

        context.DrawText(text, new Point(12, Math.Max(12, height / 2 - text.Height)));
    }

    /// <summary>
    /// 指示条旁边的时刻，<b>固定显示到毫秒</b>。
    /// </summary>
    /// <remarks>
    /// 刻意不跟着刻度步长走。刻度标签粗一点无妨，它标的是「大致在哪儿」；
    /// 这个数字标的是「播放头精确停在哪」。而拖动定位的精度是 1 像素 = 24 毫秒
    /// （每屏 18 秒时），若跟着刻度步长显示到整秒，要拖四十多个像素数字才跳一下——
    /// 反馈精度低于操作精度，精细调节就无从谈起。做时间轴的工具显示到毫秒也是惯例。
    /// </remarks>
    private static string FormatIndicatorTimestamp(double seconds)
    {
        var negative = seconds < -1e-9;
        var value = TimeSpan.FromSeconds(Math.Abs(seconds));

        var text = value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}.{value.Milliseconds:000}";

        return negative ? "-" + text : text;
    }

    private static double ChooseTickStep(double pixelsPerSecond)
    {
        var minimum = MinimumTickGapPixels / Math.Max(pixelsPerSecond, 1e-6);

        foreach (var step in TickSteps)
        {
            if (step >= minimum) return step;
        }

        return TickSteps[^1];
    }

    /// <summary>
    /// 把时间格式化成 <c>分:秒</c>；步长小于 1 秒时补一位小数，超过 1 小时则带小时。
    /// </summary>
    /// <remarks>
    /// 超限滚动时会出现负时刻，加个负号——那表示「音频开始之前」，
    /// 与「正数但超出末尾」一样都是用户刻意滚出去的位置。
    /// </remarks>
    private static string FormatTimestamp(double seconds, double step)
    {
        var negative = seconds < -1e-9;
        var value = TimeSpan.FromSeconds(Math.Abs(seconds));

        string text;
        if (value.TotalHours >= 1)
        {
            text = $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }
        else if (step < 1)
        {
            text = $"{(int)value.TotalMinutes}:{value.Seconds:00}.{value.Milliseconds / 100}";
        }
        else
        {
            text = $"{(int)value.TotalMinutes}:{value.Seconds:00}";
        }

        return negative ? "-" + text : text;
    }
}
