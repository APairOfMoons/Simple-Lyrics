namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 一行歌词及其在时间轴上的位置。
/// </summary>
/// <remarks>
/// <see cref="Start"/> 为 <c>null</c> 表示这行尚未打轴；
/// <see cref="End"/> 通常留 <c>null</c>，导出时由下一行的起点推导。
/// </remarks>
public sealed class LyricLine
{
    /// <summary>歌词正文（不含时间戳）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>该行开始时间；<c>null</c> 表示未打轴。</summary>
    public TimeSpan? Start { get; set; }

    /// <summary>该行结束时间；<c>null</c> 表示由下一行起点推导。</summary>
    public TimeSpan? End { get; set; }

    /// <summary>是否已经打上时间轴。</summary>
    public bool IsTimed => Start.HasValue;

    public override string ToString() =>
        Start is { } s ? $"[{s:mm\\:ss\\.ff}] {Text}" : $"[--:--.--] {Text}";
}
