using System.Text;

namespace SimpleLyrics.Core.Lyrics;

/// <summary>
/// 把歌词写成一个 <c>.lrc</c> 文件。
/// </summary>
/// <remarks>
/// <b>这是「保存」最常用的那个去处</b>，也是唯一现在就实现了的。编码固定 UTF-8 <b>带 BOM</b>、
/// 换行固定 <c>CRLF</c>：中文 LRC 的读者里有相当一部分按 BOM 判断编码，不带就按 GBK 猜，
/// 整份中文就花了；而这份文件的读者多半是 Windows 上的播放器与记事本。
/// </remarks>
public sealed class LrcFileSink : ILyricSink
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public LrcFileSink(string path) => Path = path;

    /// <summary>写到哪个文件。</summary>
    public string Path { get; }

    /// <summary>状态栏里只写文件名：整条路径太占地方，而同名文件放错目录是另一回事。</summary>
    public string Describe => System.IO.Path.GetFileName(Path);

    public bool CanWrite => true;

    public string? UnavailableReason => null;

    public long Write(string text)
    {
        File.WriteAllText(Path, text, Utf8WithBom);

        return Utf8WithBom.GetByteCount(text) + Utf8WithBom.GetPreamble().Length;
    }
}
