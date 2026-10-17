using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Core.Editing;

/// <summary>
/// 把一批未绑定的行换成另一批——编辑窗口按「确定」时压进来的那一步。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不逐条记。</b>窗口里的插入、删改、调顺序可以攒出几十次改动；逐条压进主历史的话，
/// 用户改完要按几十次 <c>Ctrl+Z</c> 才退得回去，而他自己也说不清「退到第几步才是对的」。
/// 折成一条之后，<c>Ctrl+Z</c> 一次就回到打开窗口之前，而窗口内部那几十步由文本框自己那套
/// 撤销管着（见 PLAN §9「未绑定歌词的编辑窗口」）。
/// </para>
/// <para>
/// <b>「追加到末尾」就够，不必记住原本的位置。</b>未绑定的行在
/// <see cref="LyricDocument.Lines"/> 里可能被已绑定的行隔开，但那个位置毫无意义：
/// 两栏是分开显示的，保存只写已绑定的行，没有任何东西依赖未绑定行夹在谁和谁之间。
/// 顺序只需在未绑定行<b>彼此之间</b>保持，而两端的数组顺序各自保证了这一点。
/// </para>
/// </remarks>
public sealed class ReplaceUnboundLinesCommand : IEditCommand
{
    private readonly LyricDocument _document;
    private readonly LyricLine[] _removed;
    private readonly LyricLine[] _added;

    /// <param name="removed">要摘掉的那些行（原来在未绑定那一侧的）。</param>
    /// <param name="added">要加上的那些行（整理之后的，顺序就是它们今后的次序）。</param>
    public ReplaceUnboundLinesCommand(
        LyricDocument document,
        IEnumerable<LyricLine> removed,
        IEnumerable<LyricLine> added)
    {
        _document = document;
        _removed = [.. removed];
        _added = [.. added];

        Label = $"整理未绑定歌词（{_removed.Length} 行 → {_added.Length} 行）";
    }

    public string Label { get; }

    public void Apply()
    {
        foreach (var line in _removed) _document.Lines.Remove(line);
        foreach (var line in _added) _document.Lines.Add(line);
    }

    public void Revert()
    {
        foreach (var line in _added) _document.Lines.Remove(line);
        foreach (var line in _removed) _document.Lines.Add(line);
    }
}
