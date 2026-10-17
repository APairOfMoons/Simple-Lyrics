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
/// <b>有 <c>Start</c> 就是已绑定，没有就是未绑定</b>——归属由行自己的字段决定，
/// 于是「绑定 / 解绑」是一次赋值，而不是把元素从一个列表搬到另一个列表。
/// 两栏因此只是同一个列表的两个视图，永远不会出现「这一行到底在哪一边」这种问题。
/// </para>
/// <para>
/// 两个派生视图都带缓存。任何改动时间的操作都必须调用 <see cref="Invalidate"/>——
/// 缓存是在第一次取用（或上一次作废之后）建立的。拖动微调正是这样一条路径：
/// 它就地改掉若干行的 <see cref="LyricLine.Start"/>，若不通知文档，已排好序的
/// <see cref="TimedLines"/> 会停在旧顺序上，屏幕上就会出现「时间改了、位置没动」。
/// </para>
/// <para>
/// <b>「这份歌词从哪儿来」「保存到哪儿去」「脏不脏」都不在这里</b>，它们属于会话
/// （<see cref="LyricSession"/>）。文档只管内容，于是换音频、另存为、撤销这些事都动不到它，
/// 它也不必知道文件系统的存在。
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
    /// 尚未打轴的行，保持原序。
    /// </summary>
    /// <remarks>
    /// 这是「未绑定」栏的数据源，也顺带回答了「下一个要盖章的是哪一行」——就是列表头那一行。
    /// 它保持文档原序，于是从时间轴上移出的行会回到它原本的位置。
    /// </remarks>
    public IReadOnlyList<LyricLine> UntimedLines =>
        _untimedLines ??= Lines.Where(static line => !line.Start.HasValue).ToList();

    /// <summary>内容变动之后丢弃派生缓存。</summary>
    public void Invalidate()
    {
        _timedLines = null;
        _untimedLines = null;
    }

    /// <summary>
    /// 清空成一份空文档。
    /// </summary>
    /// <remarks>连头部元数据一起清：那是上一份歌词的东西，留着只会在新歌词里冒出来。</remarks>
    public void Clear()
    {
        Lines.Clear();
        Metadata.Reset();
        Invalidate();
    }

    /// <summary>
    /// 把另一份文档的内容整体搬进来，<b>本实例保留</b>。
    /// </summary>
    /// <remarks>
    /// <b>为什么是搬运，而不是换一个对象。</b>文档实例从生到死只有一个，于是界面层不必关心
    /// 「文档换了没有」——它只管在改动之后重新发布那几个派生列表；控件里那些按引用认行的选区
    /// （<c>LyricTrackView.PruneSelection</c>）也有一条稳定的基准。换成替换对象的话，
    /// 每一处持有过它的地方都得记得重新取一次，而漏掉的地方不会报错，只会拿着旧文档继续算。
    /// </remarks>
    public void Adopt(LyricDocument source)
    {
        Lines.Clear();
        Lines.AddRange(source.Lines);
        Metadata.CopyFrom(source.Metadata);
        Invalidate();
    }
}
