namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// LRC 头部元数据，即形如 <c>[ti:标题]</c> 的标签。
/// </summary>
public sealed class LrcMetadata
{
    /// <summary><c>[ti:]</c> 标题。</summary>
    public string? Title { get; set; }

    /// <summary><c>[ar:]</c> 艺术家。</summary>
    public string? Artist { get; set; }

    /// <summary><c>[al:]</c> 专辑。</summary>
    public string? Album { get; set; }

    /// <summary><c>[by:]</c> 制作者。</summary>
    public string? Author { get; set; }

    /// <summary>
    /// <c>[offset:]</c> 声明的全局偏移（毫秒），正数表示整体延后。
    /// </summary>
    /// <remarks>
    /// <b>只记录，不折算进行时间。</b>本工具的时间轴以「音频真实时间」为基准，导入后显示的
    /// 秒数必须与文件里写的逐字一致——否则用户拿手里的文本对照不上，排查偏差时无从下手。
    /// LRC 的 offset 与「全局延迟补偿」本来就是同一件事的两种表达，后者由 §7.5 的打轴环节
    /// 统一承担，两处都做就会重复施加。非零时导入会在状态栏明确告知。
    /// </remarks>
    public int OffsetMilliseconds { get; set; }

    /// <summary>
    /// 本解析器不认识、但需要原样保留的标签（如 <c>[length:03:00]</c>），导出时写回。
    /// </summary>
    public Dictionary<string, string> Extra { get; } = [];

    /// <summary>除 offset 之外没有任何元数据。</summary>
    public bool IsEmpty =>
        Title is null && Artist is null && Album is null && Author is null && Extra.Count == 0;
}
