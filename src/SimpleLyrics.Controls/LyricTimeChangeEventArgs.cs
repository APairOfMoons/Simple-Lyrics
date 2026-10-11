using SimpleLyrics.Core.Editing;

namespace SimpleLyrics.Controls;

/// <summary>
/// 用户把选中的标识线拖到了新的时刻。
/// </summary>
/// <remarks>
/// 时间轴数据在拖动过程中<b>一点都没动</b>，所以这里报出去的是「应该落到哪儿」；
/// 真正写入以及「太挤就错开」的判定发生在数据层（<see cref="LyricTimeline"/>），
/// 界面层只负责把结果画出来。
/// </remarks>
public sealed class LyricTimeChangeEventArgs(IReadOnlyList<LyricTimeTarget> targets) : EventArgs
{
    /// <summary>被拖动的行与它们的目标时刻，按目标时刻升序。</summary>
    public IReadOnlyList<LyricTimeTarget> Targets { get; } = targets;
}
