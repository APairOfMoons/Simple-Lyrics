namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 一份歌词：按文件原序保存全部行，附带头部元数据。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Lines"/> 保持文件里的顺序。打轴编辑时「行的顺序」就是工作顺序
/// （光标从上一行移到下一行），解析时擅自按时间重排会把这个顺序打乱；
/// 而已打轴与未打轴的分栏、以及时间轴上的标识，都只是<b>派生视图</b>，
/// 见 <see cref="TimedLines"/> 与 <see cref="UntimedLines"/>。
/// </para>
/// <para>
/// 两个派生视图都带缓存。任何改动时间的操作都必须调用 <see cref="Invalidate"/>——
/// 缓存是在第一次取用（或上一次作废之后）建立的。拖动微调正是这样一条路径：
/// 它就地改掉若干行的 <see cref="LyricLine.Start"/>，若不通知文档，已排好序的
/// <see cref="TimedLines"/> 会停在旧顺序上，屏幕上就会出现「时间改了、位置没动」。
/// </para>
/// </remarks>
public sealed class LyricDocument
{
    private IReadOnlyList<LyricLine>? _timedLines;
    private IReadOnlyList<LyricLine>? _untimedLines;

    /// <summary>全部歌词行，保持文件原序。</summary>
    public List<LyricLine> Lines { get; } = [];

    /// <summary>头部元数据。</summary>
    public LrcMetadata Metadata { get; } = new();

    /// <summary>
    /// 这份歌词的来源文件；<c>null</c> 表示还没有源文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「有源文件」与「无源文件」在保存上的<b>唯一</b>区别就是路径从哪儿来：
    /// 前者直接用这个值写回，后者由界面层问用户要一个，拿到之后就成了有源文件。
    /// 于是将来「从零创建歌词」不需要在数据层开任何分支——
    /// 关于保存的全部逻辑都收在 <see cref="LrcWriter.Save"/> 一处，见那里的说明。
    /// </para>
    /// <para>
    /// 编辑历史（撤销栈）同样与来源无关：改动只发生在 <see cref="LyricLine.Start"/> 上，
    /// 撤销只需要记「哪几行从什么变成了什么」，不必知道文件在哪儿。
    /// </para>
    /// </remarks>
    public string? SourcePath { get; set; }

    /// <summary>相对上次保存是否有改动。</summary>
    public bool IsDirty { get; set; }

    /// <summary>
    /// 已打轴的行，按时间升序。
    /// </summary>
    /// <remarks>
    /// 时间相同的行保持原文件顺序（<c>OrderBy</c> 是稳定排序）。重复时间戳刻意<b>不去重</b>：
    /// 文件里写了两个同一时刻的标记，就应当画出两条线，合并与否交给显示层按缩放决定。
    /// </remarks>
    public IReadOnlyList<LyricLine> TimedLines =>
        _timedLines ??= Lines.Where(static line => line.Start.HasValue)
                             .OrderBy(static line => line.Start!.Value)
                             .ToList();

    /// <summary>
    /// 尚未打轴的行，保持原文件顺序。
    /// </summary>
    /// <remarks>
    /// 这是留给「未绑定」栏的接口：粘贴歌词、空格盖章、以及将来的识别对齐都会从这里取行。
    /// 本轮尚未实现那些功能，接口先立在这里，免得日后为了拿一个列表而改动数据模型。
    /// </remarks>
    public IReadOnlyList<LyricLine> UntimedLines =>
        _untimedLines ??= Lines.Where(static line => !line.Start.HasValue).ToList();

    /// <summary>内容变动之后丢弃派生缓存。</summary>
    public void Invalidate()
    {
        _timedLines = null;
        _untimedLines = null;
    }
}
