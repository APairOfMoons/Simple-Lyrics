namespace SimpleLyrics.Controls;

/// <summary>
/// 屏幕上挤在一起的一组歌词标识线。
/// </summary>
/// <param name="TopY">簇内最上面那条线的纵坐标。</param>
/// <param name="BottomY">簇内最下面那条线的纵坐标。</param>
/// <param name="Start">簇内第一行在歌词列表里的下标。</param>
/// <param name="Count">簇内包含多少行歌词。为 1 表示这一行可以单独标出文字。</param>
/// <remarks>
/// <b>存下标而不是存行本身。</b>拖动微调是按「哪几行被选中」办事的，而选中集是逐行比较的——
/// 只知道「簇里有 12 行」是不够的，得能一行一行数出来。存下标即让调用方
/// （<c>LyricTrackView</c> 与诊断工具）用自己手里那份歌词列表取行，
/// 也免得簇结构里再抄一份引用、两处慢慢对不上。
/// </remarks>
public readonly record struct LyricMarkCluster(double TopY, double BottomY, int Start, int Count)
{
    /// <summary>簇内最后一行的下标（不含）。</summary>
    public int End => Start + Count;

    /// <summary>某一行是否属于这一簇。</summary>
    public bool Contains(int index) => index >= Start && index < End;
}
