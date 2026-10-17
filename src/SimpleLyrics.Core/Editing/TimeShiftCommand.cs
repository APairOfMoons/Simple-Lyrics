using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Core.Editing;

/// <summary>一行歌词的时间码从哪儿变到了哪儿。</summary>
/// <param name="Line">被改动的行。</param>
/// <param name="Before">改动之前的时刻（<c>null</c> 表示原本未打轴）。</param>
/// <param name="After">改动之后的时刻（<c>null</c> 表示这一行被移出了时间轴）。</param>
public readonly record struct LyricTimeChange(LyricLine Line, TimeSpan? Before, TimeSpan? After);

/// <summary>
/// 一行歌词的时间码被改动所留下的那一条撤销记录。
/// </summary>
/// <remarks>
/// <para>
/// 记的是「哪几行、从什么变成了什么」，而不是「拖了多少毫秒」：后者在撤销时要求
/// 时间码一点没被别的东西动过，而前者无论中间发生过什么都能精确还原。
/// 错开（<see cref="LyricTimeline"/> 把挨得太近的行推开）的结果也一并记在里面，
/// 于是撤销一次就回到了拖动之前的样子，不会留下一个「错开还在、拖动没了」的中间态。
/// </para>
/// <para>
/// <b>时刻的两端都可空，于是「绑定」与「解绑」都落进了这一个命令。</b>
/// 把一行从未打轴变成某个时刻（将来的空格盖章）、或者从某个时刻变回未打轴
/// （把一行移出「已绑定」栏），在数据上都是「<c>Start</c> 从 A 变成 B」，
/// 只是 A 或 B 恰好是 <c>null</c>。解绑若另起一个命令，撤销时就得判断「上一次撤的
/// 是拖动还是解绑」，而两条路的差别只在那一个赋值上——没有差别的分支迟早会走岔。
/// </para>
/// <para>
/// 只有<b>真的变了</b>的行才进这份清单：一次没挪动的拖动不该占用一步撤销，
/// 否则用户按一次 Ctrl+Z 会看起来毫无反应。
/// </para>
/// </remarks>
public sealed class TimeShiftCommand : IEditCommand
{
    private readonly LyricTimeChange[] _changes;

    /// <param name="changes">逐行的前后值，只包含真的变了的行。</param>
    /// <param name="label">
    /// 状态栏上那句话。默认按「调整时间码」措辞，而移出时间轴之类的用途可以另给一句——
    /// 命令自己不去猜这次改动是什么性质：它只看得到前后值，而「解绑」这个说法属于界面。
    /// </param>
    public TimeShiftCommand(IEnumerable<LyricTimeChange> changes, string? label = null)
    {
        _changes = [.. changes];
        Label = label ?? $"调整 {_changes.Length} 行歌词的时间码";
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
