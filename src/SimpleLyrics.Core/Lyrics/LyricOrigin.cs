namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 这份歌词是从哪儿来的。
/// </summary>
/// <remarks>
/// <para>
/// <b>记在会话上，而不是每一行上。</b>每次载入都是整份替换（见 <see cref="LyricSession"/>），
/// 所以一份文档里不可能混着两种来源的行——行级的来源标记于是没有用途，
/// 而它一旦存在，就得回答「粘贴的行被盖章之后算谁的」这类没有意义的问题。
/// </para>
/// <para>
/// 它换来的东西有两样：状态栏上说得清「这份歌词是随音频带进来的，还是你贴进来的」，
/// 以及「放弃改动」（<c>Ctrl+Shift+D</c>）知道该回哪儿去重读。
/// </para>
/// </remarks>
public enum LyricOrigin
{
    /// <summary>还没有歌词。</summary>
    None,

    /// <summary>粘贴进来的纯文本：只有文本稿，没有时间轴。</summary>
    Pasted,

    /// <summary>随音频自动载入的同名 <c>.lrc</c>（侧车）。</summary>
    Sidecar,

    /// <summary>用户手动导入的 <c>.lrc</c> 文件。</summary>
    LrcFile,

    /// <summary>音频文件里内嵌的歌词标签（尚未实现，见 <see cref="LyricImport.TryLoadEmbedded"/>）。</summary>
    Embedded,
}
