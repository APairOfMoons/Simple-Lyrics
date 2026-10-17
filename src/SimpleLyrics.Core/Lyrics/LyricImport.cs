namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 歌词从哪儿来：三种读入方式，外加一个尚未实现的。
/// </summary>
/// <remarks>
/// <para>
/// 三种来源的差别只在<b>去哪儿找</b>与<b>找不着时怎么办</b>，读出来之后都是一份
/// <see cref="LyricDocument"/>。把它们收在这里而不是散在界面层，是为了让
/// 「打开音频后自动找歌词」那条链路能写成<b>一条</b>：
/// 先问侧车、再问内嵌、都没有就留空——将来补上内嵌的实现时，
/// 流程、界面、状态栏措辞一个字都不用动。
/// </para>
/// <para>
/// 粘贴不在这里：它不是「读一个地方」，而是用户直接给的一段文本，
/// 由 <see cref="LyricSession.ImportText"/> 处理。
/// </para>
/// </remarks>
public static class LyricImport
{
    /// <summary>
    /// 侧车：与音频同名、同目录的 <c>.lrc</c>。
    /// </summary>
    /// <remarks>
    /// 「没有」与「有但读不了」要分开：前者是常态，一个字都不该说；后者必须说出来，
    /// 否则用户会以为这首歌本来就没有歌词，然后疑惑自己昨天写的那份去哪儿了。
    /// </remarks>
    /// <param name="path">找到的那个文件；没找到时是 <c>null</c>。</param>
    /// <param name="warning">读不了时的原因；一切正常或压根没有侧车时是 <c>null</c>。</param>
    /// <returns>载入的文档；没有或读不了时是 <c>null</c>。</returns>
    public static LyricDocument? TryLoadSidecar(string? audioPath, out string? path, out string? warning)
    {
        warning = null;
        path = LrcWriter.SuggestPath(audioPath);

        if (path is null || !File.Exists(path))
        {
            path = null;
            return null;
        }

        try
        {
            return LrcParser.Load(path, out warning);
        }
        catch (Exception ex)
        {
            warning = $"侧车歌词读不了（{ex.Message}）";
            return null;
        }
    }

    /// <summary>
    /// 用户手动指定的 <c>.lrc</c> 文件。
    /// </summary>
    /// <remarks>抛出去：这是用户明确点的文件，读不了应当让他看见失败，而不是静默留空。</remarks>
    public static LyricDocument LoadFile(string path, out string? warning) => LrcParser.Load(path, out warning);

    /// <summary>
    /// 音频文件里内嵌的歌词标签（USLT / Vorbis LYRICS / ©lyr）。
    /// </summary>
    /// <remarks>
    /// <b>尚未实现</b>：现在一律返回 <c>false</c>，原因写在 <paramref name="reason"/> 里。
    /// 之所以现在就把入口立出来，是因为「打开音频后自动找歌词」要按两种来源写；
    /// 实现时改动只落在本方法的函数体里。写入那一侧在
    /// <see cref="Embedded.EmbeddedLyricSink"/>，两者将来共用同一套标签知识。
    /// </remarks>
    public static bool TryLoadEmbedded(string? audioPath, out LyricDocument? document, out string reason)
    {
        document = null;

        reason = audioPath is null
            ? "还没有打开音频"
            : "音频内嵌歌词尚未实现 —— 计划读 USLT / Vorbis LYRICS / ©lyr（v2）";

        return false;
    }
}
