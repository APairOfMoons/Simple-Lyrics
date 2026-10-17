using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Controls;

/// <summary>
/// 未绑定歌词栏：一个从上往下排的歌词栈，栈顶紧贴在播放指示线的<b>上方</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是一个「栈」而不是一张列表。</b>这些行还没有时刻，按时间排是排不出来的；
/// 它们唯一的次序就是「打轴的先后」——下一个要盖章的是哪一行，是这个界面上最要紧的一件事。
/// 所以栈顶（第一行）单独摆在指示线上方，紧贴着那条线，像一枚已经上膛的子弹；
/// 其余的按正序从线下方向下排开，等着被推上去。
/// </para>
/// <para>
/// <b>指示线就是左栏那条线，不是另一条。</b>波形控件画一条横贯波形栏的琥珀色水平线，
/// 位置是「高度比例 × 控件高度」；本栏画的是同一个比例下的同一条线。三栏同高（它们在
/// 同一行里），于是两栏的线严格对齐——用户在右栏看到的正是左栏播放头所在的高度。
/// 线的颜色与粗细直接取自 <see cref="WaveformView"/>，两处各写一份的话，
/// 改一处就会让它们悄悄分家，而「看上去不是同一条线」这种缺陷只能靠眼睛发现。
/// </para>
/// <para>
/// <b>没有时间轴参数，也不需要。</b>本栏与滚动、缩放完全无关：栈的次序由文档决定，
/// 行的位置由序号决定。这也是它与 <see cref="LyricTrackView"/> 的根本差别——
/// 那边的一切判据都建立在「时刻 × 每秒像素数」上。
/// </para>
/// </remarks>
public sealed class UnboundLyricView : Control
{
    private const double TitleFontSize = 14;
    private const double SubtitleFontSize = 11;
    private const double BodyFontSize = 13;
    private const double IndexFontSize = 11;

    /// <summary>栏内左边缘到内容之间的距离，与另外几栏的标题取同一个数。</summary>
    private const double LeftPadding = 22;

    private const double TitleTop = 20;

    /// <summary>标题与副标题之间、以及副标题与顶端之间的空隙。</summary>
    private const double TitleGap = 4;

    /// <summary>序号那一列的宽度，含它与正文之间的空隙。</summary>
    private const double IndexWidth = 26;

    /// <summary>正文右侧留白，免得文字贴着栏的边框。</summary>
    private const double RightPadding = 22;

    /// <summary>行与行之间的空隙。</summary>
    private const double RowSpacing = 6;

    /// <summary>指示线与它上方的栈顶行、下方的第二行之间各留的空隙。</summary>
    private const double StackGap = 7;

    /// <summary>
    /// 底部留给「还有 N 行」这句话的高度。
    /// </summary>
    /// <remarks>
    /// 算进可用高度里，是为了让最后一行歌词不会与那句话叠在一起——
    /// 叠上去的表现是两段文字糊成一团，而它偏偏出现在歌词最多、最需要看清的时候。
    /// </remarks>
    private const double OverflowHintHeight = 22;

    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4));
    private static readonly IBrush SubtitleBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x78, 0x89));
    private static readonly IBrush IndexBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x78, 0x89));
    private static readonly IBrush BodyBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0xF2, 0xF8));

    /// <summary>
    /// 栈顶那一行的颜色：歌词本体的粉色。
    /// </summary>
    /// <remarks>
    /// 栈顶与下面排队的行在语义上确实不同——它是「下一个要盖章的」，其它只是等待。
    /// 用粉色（已绑定栏里标识线的颜色）标它，含义正好接得上：这一行即将成为时间轴上的一条线。
    /// 序号也跟着一起变色，因为「第几行」正是这里的坐标。
    /// </remarks>
    private static readonly IBrush TopBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0xD4));

    private static double? _lineHeight;

    /// <summary>
    /// 一行正文的高度（像素），由当前字号实测得出。
    /// </summary>
    /// <remarks>公开是给诊断工具用的：它要核对栈顶是否真的落在指示线上方，就得知道一行有多高。</remarks>
    public static double LineHeight => _lineHeight ??= MeasureHeight("字", BodyFontSize);

    /// <summary>
    /// 第 <paramref name="index"/> 行在控件内的<b>行顶</b>纵坐标（0 是栈顶）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 栈顶那一行整体排在指示线<b>上方</b>（它的行底离线的距离就是 <see cref="StackGap"/>），
    /// 其余各行从线下方依次向下，行距是「一行高 + <see cref="RowSpacing"/>」。
    /// </para>
    /// <para>
    /// 公开出来，是为了让诊断工具用<b>同一份</b>实现核对这条排布——工具若自己再写一遍
    /// 那几项间距，改天调一下间距就悄悄脱节了，而脱节的表现是「测出来的栈没问题、
    /// 屏幕上的栈歪了」。这与 <see cref="LyricTrackView.GroupInto"/> 公开出来的理由相同。
    /// </para>
    /// </remarks>
    public static double RowTop(int index, double indicatorY) =>
        index == 0
            ? indicatorY - StackGap - LineHeight
            : indicatorY + StackGap + (index - 1) * (LineHeight + RowSpacing);

    /// <summary>
    /// 未绑定的歌词行，<b>按文档原序</b>（也就是导入的顺序）。
    /// </summary>
    /// <remarks>
    /// 顺序不能在这里再排一遍。取的就是 <see cref="LyricDocument.UntimedLines"/>：
    /// 「栈里的次序」＝「文档里这些行的先后」这一条只由数据层说了算，
    /// 显示层若自己按正文或别的什么排一次，行与行之间就再也没有稳定的对应关系了——
    /// 而用户按空格盖章时，章的正是「栈顶那一行」。
    /// </remarks>
    public static readonly StyledProperty<IReadOnlyList<LyricLine>?> LyricsProperty =
        AvaloniaProperty.Register<UnboundLyricView, IReadOnlyList<LyricLine>?>(nameof(Lyrics));

    /// <summary>
    /// 播放指示器的高度比例（0 到 1），与 <see cref="WaveformView.IndicatorRatio"/> 取同一个值。
    /// </summary>
    public static readonly StyledProperty<double> IndicatorRatioProperty =
        AvaloniaProperty.Register<UnboundLyricView, double>(nameof(IndicatorRatio), 0.4);

    /// <summary>
    /// 画不画那条播放指示线。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由外面告诉它，而不是它自己去打听有没有音频：本控件在 Controls 层，不认识音频、
    /// 也不该认识。判据在波形控件那边（<see cref="WaveformView.HasWaveform"/>），
    /// 由窗口的代码后置同步过来——与 <see cref="IndicatorRatio"/> 走的是同一条路。
    /// </para>
    /// <para>
    /// <b>默认 <c>false</c>。</b>宁可少画一条，也不要在一个还没有音频的窗口里画出一条
    /// 谁也不知道它标记着什么的线：它标的是「播放头在哪儿」，而没有音频就没有播放头。
    /// 忘了同步的表现于是是「这条线一直不出现」，一眼就能发现；反过来忘了同步，
    /// 表现是那条线永远在，反而看着挺正常。
    /// </para>
    /// <para>
    /// <b>不画线时栈的位置照样按同一个比例算。</b>否则打开音频的那一瞬间，
    /// 整栈歌词会从顶上跳下来——那是「一下子全变了」的那种抖动，不值得为一条线的显隐付。
    /// </para>
    /// </remarks>
    public static readonly StyledProperty<bool> ShowIndicatorProperty =
        AvaloniaProperty.Register<UnboundLyricView, bool>(nameof(ShowIndicator));

    /// <summary>还没有未绑定歌词时显示的提示文字。</summary>
    public static readonly StyledProperty<string?> EmptyHintProperty =
        AvaloniaProperty.Register<UnboundLyricView, string?>(nameof(EmptyHint));

    static UnboundLyricView()
    {
        AffectsRender<UnboundLyricView>(LyricsProperty, IndicatorRatioProperty, ShowIndicatorProperty, EmptyHintProperty);
    }

    public UnboundLyricView()
    {
        // 这一栏不接收指针事件（这一轮里它还没有可操作的东西），
        // 于是滚轮会落到它下面那条 Border 上——将来若要给它加滚动或点击，改这里一处即可。
        IsHitTestVisible = false;
        Focusable = false;
    }

    public IReadOnlyList<LyricLine>? Lyrics
    {
        get => GetValue(LyricsProperty);
        set => SetValue(LyricsProperty, value);
    }

    public double IndicatorRatio
    {
        get => GetValue(IndicatorRatioProperty);
        set => SetValue(IndicatorRatioProperty, value);
    }

    public bool ShowIndicator
    {
        get => GetValue(ShowIndicatorProperty);
        set => SetValue(ShowIndicatorProperty, value);
    }

    public string? EmptyHint
    {
        get => GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        using (context.PushClip(new Rect(0, 0, width, height)))
        {
            DrawHeader(context, width);
            DrawIndicator(context, width, height);

            if (Lyrics is not { Count: > 0 } lyrics)
            {
                TrackText.DrawHint(context, EmptyHint, 0, width, height);
                return;
            }

            DrawStack(context, width, height, lyrics);
        }
    }

    private static void DrawHeader(DrawingContext context, double width)
    {
        var available = width - LeftPadding * 2;

        var title = TrackText.Truncated("未绑定", TitleBrush, TitleFontSize, available);
        context.DrawText(title, new Point(LeftPadding, TitleTop));

        var subtitle = TrackText.Truncated("等待打轴的歌词行", SubtitleBrush, SubtitleFontSize, available);
        context.DrawText(subtitle, new Point(LeftPadding, TitleTop + title.Height + TitleGap));
    }

    /// <summary>
    /// 画那条与左栏对齐的播放指示线。
    /// </summary>
    /// <remarks>
    /// 辉光与主线两层都要画：左栏画的也是这两层（它压在波形上，靠辉光在密集的波形里被找到）。
    /// 只画一层的话，两栏的线在视觉上就不是同一条了——这恰恰是本栏画它的唯一理由。
    /// 没有音频时整个不画，理由见 <see cref="ShowIndicatorProperty"/>。
    /// </remarks>
    private void DrawIndicator(DrawingContext context, double width, double height)
    {
        if (!ShowIndicator) return;

        var y = Math.Clamp(IndicatorRatio, 0, 1) * height;

        context.DrawLine(WaveformView.IndicatorGlowPen, new Point(0, y), new Point(width, y));
        context.DrawLine(WaveformView.IndicatorPen, new Point(0, y), new Point(width, y));
    }

    /// <summary>
    /// 把栈画出来：栈顶在指示线上方，其余从线下方依次向下。
    /// </summary>
    private void DrawStack(DrawingContext context, double width, double height, IReadOnlyList<LyricLine> lyrics)
    {
        var bodyWidth = width - LeftPadding - IndexWidth - RightPadding;
        if (bodyWidth < 24) return;

        var rowHeight = LineHeight;

        var indicatorY = Math.Clamp(IndicatorRatio, 0, 1) * height;
        var limit = height - OverflowHintHeight;

        var hidden = 0;

        for (var index = 0; index < lyrics.Count; index++)
        {
            var top = RowTop(index, indicatorY);

            // 装不下的行不画，但也不静默丢掉：底下会有一行字说明还有多少行没显示出来。
            if (top + rowHeight > limit || top < 0)
            {
                hidden++;
                continue;
            }

            DrawRow(context, lyrics[index], index, top, bodyWidth);
        }

        if (hidden == 0) return;

        var hint = TrackText.Truncated(
            $"还有 {hidden} 行没显示出来（栈里共 {lyrics.Count} 行）",
            SubtitleBrush, IndexFontSize, width - LeftPadding - RightPadding);

        context.DrawText(hint, new Point(LeftPadding, limit + 2));
    }

    private static void DrawRow(DrawingContext context, LyricLine line, int index, double top, double bodyWidth)
    {
        var isTop = index == 0;

        var number = TrackText.Truncated(
            (index + 1).ToString(CultureInfo.InvariantCulture),
            isTop ? TopBrush : IndexBrush,
            IndexFontSize,
            IndexWidth - 6);

        // 序号右对齐，于是「9」与「10」的正文起点在同一处——左对齐的话每多一位数就会把正文挤歪。
        number.TextAlignment = TextAlignment.Right;

        context.DrawText(number, new Point(LeftPadding, top + (LineHeight - number.Height) / 2));

        var body = TrackText.Truncated(line.Text, isTop ? TopBrush : BodyBrush, BodyFontSize, bodyWidth);
        context.DrawText(body, new Point(LeftPadding + IndexWidth, top + (LineHeight - body.Height) / 2));
    }

    private static double MeasureHeight(string text, double fontSize) =>
        new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, fontSize, BodyBrush).Height;
}
