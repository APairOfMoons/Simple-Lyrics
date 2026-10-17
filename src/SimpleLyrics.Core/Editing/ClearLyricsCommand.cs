using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Core.Editing;

/// <summary>
/// 清空整份歌词（<c>Ctrl+N</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 做成一步可撤销的改动，而不是弹一次「真的要清空吗」：撤销框架已经在了，
/// 用它换掉一个确认对话框是最划算的——按错了只要 <c>Ctrl+Z</c>。
/// </para>
/// <para>
/// 它<b>不</b>负责清来源、主去处与保存点：那些是 <see cref="LyricSession"/> 的事，
/// 而「换音频」那条路要连它们一起清掉、并且连撤销栈一起清。命令只管文档里的内容，
/// 这样它才能被撤销。
/// </para>
/// </remarks>
public sealed class ClearLyricsCommand : IEditCommand
{
    private readonly LyricDocument _document;
    private readonly LyricLine[] _lines;
    private readonly LrcMetadata _metadata = new();

    public ClearLyricsCommand(LyricDocument document)
    {
        _document = document;
        _lines = [.. document.Lines];
        _metadata.CopyFrom(document.Metadata);

        Label = _lines.Length == 0 ? "清空歌词" : $"清空歌词（{_lines.Length} 行）";
    }

    public string Label { get; }

    public void Apply() => _document.Clear();

    /// <summary>
    /// 把行与头部元数据都装回去。
    /// </summary>
    /// <remarks>
    /// 行按<b>原顺序</b>追加：撤销是逆着历史走的，所以走到这里时文档必定是空的
    /// （清空之后若又有了别的改动，那些改动会先被撤销掉，而新的改动又会清空重做栈）。
    /// 于是「追加」得到的就是原来的顺序，不必去记每行原本夹在谁和谁之间。
    /// </remarks>
    public void Revert()
    {
        _document.Lines.AddRange(_lines);
        _document.Metadata.CopyFrom(_metadata);
    }
}
