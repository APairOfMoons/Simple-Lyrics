using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Core.Editing;

/// <summary>一行歌词的时间码从哪儿变到了哪儿。</summary>
/// <param name="Line">被改动的行。</param>
/// <param name="Before">改动之前的时刻（<c>null</c> 表示原本未打轴）。</param>
/// <param name="After">改动之后的时刻。</param>
public readonly record struct LyricTimeChange(LyricLine Line, TimeSpan? Before, TimeSpan After);

/// <summary>
/// 拖动微调留下的那一条撤销记录。
/// </summary>
/// <remarks>
/// <para>
/// 记的是「哪几行、从什么变成了什么」，而不是「拖了多少毫秒」：后者在撤销时要求
/// 时间码一点没被别的东西动过，而前者无论中间发生过什么都能精确还原。
/// 错开（<see cref="LyricTimeline"/> 把挨得太近的行推开）的结果也一并记在里面，
/// 于是撤销一次就回到了拖动之前的样子，不会留下一个「错开还在、拖动没了」的中间态。
/// </para>
/// <para>
/// 只有<b>真的变了</b>的行才进这份清单：一次没挪动的拖动不该占用一步撤销，
/// 否则用户按一次 Ctrl+Z 会看起来毫无反应。
/// </para>
/// </remarks>
public sealed class TimeShiftCommand : IEditCommand
{
    private readonly LyricTimeChange[] _changes;

    public TimeShiftCommand(IEnumerable<LyricTimeChange> changes)
    {
        _changes = [.. changes];
        Label = $"调整 {_changes.Length} 行歌词的时间码";
    }

    public string Label { get; }

    public void Apply()
    {
        foreach (var change in _changes) change.Line.Start = change.After;
    }

    public void Revert()
    {
        foreach (var change in _changes) change.Line.Start = change.Before;
    }
}
