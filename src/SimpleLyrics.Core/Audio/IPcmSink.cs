namespace SimpleLyrics.Core.Audio;

/// <summary>
/// 解码输出的落点：每解出一段 PCM 就回调一次。
/// </summary>
/// <remarks>
/// 刻意做成「推」而不是「拉」（不实现 <c>Stream</c>），原因有二：
/// <list type="number">
/// <item>解码器内部有帧边界，拉取式接口要么额外缓冲、要么产生大量小读取；</item>
/// <item>峰值生成只需要顺序扫一遍，推模型与它天然契合，也不要求实时性。</item>
/// </list>
/// 后续做播放时会在本接口之上再包一层环形缓冲，而不是改动解码器。
/// </remarks>
public interface IPcmSink
{
    /// <summary>
    /// 接收一段交错浮点 PCM。
    /// </summary>
    /// <param name="samples">
    /// 交错样本，长度为 <c>frameCount * 声道数</c>。
    /// **该 Span 只在本次调用内有效**，实现方若需留存数据必须自行复制。
    /// </param>
    /// <param name="frameCount">帧数（每声道的样本数）。</param>
    void Write(ReadOnlySpan<float> samples, int frameCount);
}
