namespace SimpleLyrics.Core.Editing;

/// <summary>
/// 一次可撤销的改动。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么先立这个接口。</b>改时间码只是第一件需要撤销的事：后面还有粘贴歌词、空格盖章、
/// 批量偏移、全局延迟补偿——它们全都属于「改了数据、用户可能想退回去」。与其每来一件就在
/// 界面上挂一个「撤销上一步」的专用函数，不如让每一件都变成一个命令对象，
/// 撤销与重做只认这一个接口。
/// </para>
/// <para>
/// 命令<b>只记录前后值，不记录改动是怎么算出来的</b>。于是撤销与重做永远是对称的：
/// <see cref="Revert"/> 恰好把 <see cref="Apply"/> 写下去的那些值还原回去。
/// 反过来若让命令在撤销时重新算一遍（比如「再跑一次错开」），撤销就走上了另一条代码路径，
/// 两条路迟早对不上——而对不上的表现是数据被静默改成了第三种样子。
/// </para>
/// <para>
/// 命令<b>不碰文档的派生缓存</b>（<c>LyricDocument.Invalidate</c>）。改完由调用方统一
/// 作废并重新发布：哪些缓存需要作废是文档自己的事，命令不必知道，也就不会漏。
/// </para>
/// </remarks>
public interface IEditCommand
{
    /// <summary>一句话说明这次改动，用于状态栏（「已撤销：调整 3 行歌词的时间码」）。</summary>
    string Label { get; }

    /// <summary>把改动落下去。首次执行与重做走的是同一条路径。</summary>
    void Apply();

    /// <summary>还原回去。</summary>
    void Revert();
}
