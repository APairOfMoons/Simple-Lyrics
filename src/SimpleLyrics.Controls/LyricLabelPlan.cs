namespace SimpleLyrics.Controls;

/// <summary>
/// 一条歌词标签的排版结果。
/// </summary>
/// <param name="Rows">这条标签会占几行（含与时间戳同处第一行的那段正文）。</param>
/// <param name="Truncated">正文没有画完，最后一行以省略号收尾。</param>
public readonly record struct LyricLabelPlan(int Rows, bool Truncated);
