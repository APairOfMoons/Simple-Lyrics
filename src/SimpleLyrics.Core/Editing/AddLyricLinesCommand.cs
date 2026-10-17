using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Core.Editing;

/// <summary>
/// 往文档末尾追加若干行歌词，也就是「粘贴歌词文本」这一步留下的撤销记录。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么它不是 <see cref="TimeShiftCommand"/> 的特例。</b>那一条记的是「某一行的时刻
/// 从什么变成了什么」，而行本身一直在文档里；这一条动的是<b>文档里有哪几行</b>。
/// 两者要还原的东西不一样，硬凑成一个命令就得在里面分两种情形，而分开之后各自都只有几行。
/// </para>
/// <para>
/// 追加进来的行没有时间码（<see cref="LyricLine.Start"/> 为 <c>null</c>），
/// 于是它们自动落进 <see cref="LyricDocument.UntimedLines"/>，也就是界面上的「未绑定」栏——
/// 命令不必知道「未绑定栏」这回事，那是数据模型派生出来的东西。
/// </para>
/// </remarks>
public sealed class AddLyricLinesCommand : IEditCommand
{
    private readonly LyricDocument _document;
    private readonly LyricLine[] _lines;

    public AddLyricLinesCommand(LyricDocument document, IEnumerable<LyricLine> lines)
    {
        _document = document;
        _lines = [.. lines];
        Label = $"导入 {_lines.Length} 行歌词";
    }

    public string Label { get; }

    public void Apply()
    {
        foreach (var line in _lines) _document.Lines.Add(line);
    }

    /// <summary>
    /// 按<b>引用</b>把这几行摘出去，而不是「砍掉末尾这么多个」。
    /// </summary>
    /// <remarks>
    /// 撤销与重做之间可能已经有别的改动落进文档（比如用户先撤了导入，又拖了一次时间码，
    /// 再重做导入），那时「末尾」指的已经不是这几行了。按引用移除则与别处发生过什么无关，
    /// 而 <see cref="LyricLine"/> 没有重写 <c>Equals</c>，引用相等正是列表查找的默认语义。
    /// </remarks>
    public void Revert()
    {
        foreach (var line in _lines) _document.Lines.Remove(line);
    }
}
