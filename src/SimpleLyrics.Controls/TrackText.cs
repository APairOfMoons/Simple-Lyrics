using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace SimpleLyrics.Controls;

/// <summary>
/// 歌词两栏（已绑定 / 未绑定）上文字与提示的公共画法。
/// </summary>
/// <remarks>
/// <para>
/// 这两栏是同一个界面上同一类东西，字号、颜色、截断方式都必须一致——只要有一处不同，
/// 看上去就像其中一栏出了问题。所以「一段带省略号截断的单行文字」与「空栏里那句居中提示」
/// 收在这里，而不是两栏各写一份。
/// </para>
/// <para>
/// 时间轴的排布（换行、分簇、行数）不在这里：那是已绑定栏独有的东西，未绑定栏没有时间码。
/// </para>
/// </remarks>
internal static class TrackText
{
    private const double HintFontSize = 12.5;

    private static readonly IBrush HintBrush =
        new SolidColorBrush(Color.FromArgb(0x8C, 0xD8, 0xDF, 0xEC));

    /// <summary>
    /// 造一段带省略号截断的单行文字。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>两处上限都不能省。</b><see cref="FormattedText.MaxTextWidth"/> 只设宽度的话，
    /// 过长的文字是<b>换行</b>而不是截断（<c>Trimming</c> 只在单行排版下才起作用）；
    /// 而 <see cref="FormattedText.MaxLineCount"/> 钉死行数是更硬的保证：
    /// 无论文字多长、栏多窄，<see cref="FormattedText.Height"/> 都等于一行的高度，
    /// 按行高做的那些算术才有依据。这条规则在这里踩过一次坑（长句在自己的行里又折了一层，
    /// 把所有按行高推出来的门槛全部作废），所以它只写在<b>这一处</b>。
    /// </para>
    /// <para>
    /// 宽度上留一个 16 像素的下限：栏被挤得极窄时，<c>MaxTextWidth</c> 取到 0 或负数
    /// 会让排版退化成不可预期的样子，而那时本来就什么都读不出来了。
    /// </para>
    /// </remarks>
    internal static FormattedText Truncated(string text, IBrush brush, double fontSize, double maxWidth) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, fontSize, brush)
        {
            MaxTextWidth = Math.Max(16, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    /// <summary>
    /// 画空栏里那句提示。
    /// </summary>
    /// <remarks>
    /// 提示在<b>栏内</b>居中，而不是在整个控件内居中：已绑定栏那条边界左边是波形，
    /// 提示若按控件宽度居中，一半会压到波形上去。
    /// </remarks>
    /// <param name="text">提示文字，可以带换行。</param>
    /// <param name="left">所在栏的左边界（控件内的横坐标）。</param>
    /// <param name="controlWidth">控件宽度。</param>
    /// <param name="height">控件高度。</param>
    internal static void DrawHint(DrawingContext context, string? text, double left, double controlWidth, double height)
    {
        if (string.IsNullOrEmpty(text)) return;

        var x = Math.Max(left, 0);

        // 左右各留一点空，窄栏里宁可整句不画，也不画成被裁掉半行的一团字。
        var available = controlWidth - x - 48;
        if (available < 40) return;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            HintFontSize,
            HintBrush)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = available,
        };

        context.DrawText(formatted, new Point(x + 24, Math.Max(30, height / 2 - formatted.Height / 2)));
    }
}
