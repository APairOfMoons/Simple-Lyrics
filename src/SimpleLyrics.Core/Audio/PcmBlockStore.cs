namespace SimpleLyrics.Core.Audio;

/// <summary>
/// 分块存放的交错浮点 PCM：支持按帧随机读取、向后追加、以及从最早一端丢弃。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用一个连续大数组</b>：一是解码完成之前拿不到权威总帧数（ADTS AAC 之类的裸流，
/// 容器声明时长实测能偏二十多秒），大数组必须先猜后长，而这里边解边加天然正确；
/// 二是一块的大小被刻意压在 <b>85,000 字节</b>的大对象堆阈值以下——超过它的数组会落在
/// 不压缩的大对象堆上，反复打开不同长度的文件会让那里持续增长。
/// </para>
/// <para>
/// <b>为什么块大小取 8192 帧</b>：这是 2 的幂，块号与块内偏移可以直接用移位和掩码算出来。
/// 块字节数只与声道数有关（<c>8192 × 声道数 × 4</c>），立体声恰好 65,536 字节；
/// 折算成时长则随采样率变化，44.1 kHz 下约 186 毫秒、192 kHz 下约 43 毫秒。
/// </para>
/// <para>
/// <b>本类不加锁</b>。它是被 <see cref="AudioPlayer"/> 的同一把锁罩着的内部结构，
/// 在这里再套一层锁只会让「先查区间再读数据」这种复合操作变得无法原子化。
/// </para>
/// </remarks>
public sealed class PcmBlockStore
{
    /// <summary>每块的帧数。</summary>
    public const int BlockFrames = 8192;

    private readonly List<float[]> _blocks = [];

    /// <summary>已写入的总帧数，也就是缓存区间的开区间上界。</summary>
    private long _decodedFrames;

    /// <summary><c>_blocks[0]</c> 对应的绝对块号。丢弃头部若干块后它不再为 0。</summary>
    private long _firstBlockIndex;

    public PcmBlockStore(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>交错样本的字节数。</summary>
    public int BlockAlign => Channels * sizeof(float);

    /// <summary>缓存区间起始帧（含）。</summary>
    public long FirstFrame => _firstBlockIndex * BlockFrames;

    /// <summary>缓存区间结束帧（不含）。</summary>
    public long EndFrame => _decodedFrames;

    /// <summary>当前缓存的帧数。</summary>
    public long CachedFrames => _decodedFrames - FirstFrame;

    /// <summary>已占用的字节数（含最后一块中尚未使用的尾部空间）。</summary>
    public long ApproximateBytes => (long)_blocks.Count * BlockFrames * BlockAlign;

    /// <summary>缓存区间起始时刻（秒）。</summary>
    public double FirstSeconds => (double)FirstFrame / SampleRate;

    /// <summary>缓存区间结束时刻（秒）。</summary>
    public double EndSeconds => (double)EndFrame / SampleRate;

    /// <summary>某个时刻是否落在已解码的区间内。</summary>
    public bool Covers(double seconds)
    {
        var frame = (long)(seconds * SampleRate);
        return frame >= FirstFrame && frame < _decodedFrames;
    }

    /// <summary>
    /// 把一段交错 PCM 追加到末尾。
    /// </summary>
    /// <remarks>
    /// 长度不要求对齐到块边界——跨块的部分会在这里切开写进相邻的块，
    /// 因此调用方（解码器）不必关心块的存在。
    /// </remarks>
    public void Append(ReadOnlySpan<float> samples, int frameCount)
    {
        if (frameCount <= 0) return;
        if (samples.Length < (long)frameCount * Channels)
            throw new ArgumentException("样本长度与帧数、声道数不一致", nameof(samples));

        var channels = Channels;
        var consumedFrames = 0;
        var consumedSamples = 0;

        while (consumedFrames < frameCount)
        {
            var blockIndex = _decodedFrames / BlockFrames;
            var offsetInBlock = (int)(_decodedFrames % BlockFrames);
            var take = Math.Min(frameCount - consumedFrames, BlockFrames - offsetInBlock);

            var block = GetOrCreateBlock(blockIndex);
            samples.Slice(consumedSamples, take * channels)
                   .CopyTo(block.AsSpan(offsetInBlock * channels));

            _decodedFrames += take;
            consumedFrames += take;
            consumedSamples += take * channels;
        }
    }

    /// <summary>
    /// 从 <paramref name="frameIndex"/> 开始读取若干帧到 <paramref name="destination"/>。
    /// </summary>
    /// <returns>实际读到的帧数；起点不在缓存区间内时返回 0。</returns>
    public int Read(long frameIndex, Span<float> destination, int frameCount)
    {
        if (frameCount <= 0) return 0;
        if (frameIndex < FirstFrame || frameIndex >= _decodedFrames) return 0;

        var channels = Channels;

        // 可用量同时受「缓存末尾」和「调用方给的缓冲大小」限制。
        var available = (int)Math.Min(frameCount, _decodedFrames - frameIndex);
        available = Math.Min(available, destination.Length / channels);
        if (available <= 0) return 0;

        var written = 0;
        while (written < available)
        {
            var position = frameIndex + written;
            var block = _blocks[(int)(position / BlockFrames - _firstBlockIndex)];
            var offsetInBlock = (int)(position % BlockFrames);
            var take = Math.Min(available - written, BlockFrames - offsetInBlock);

            block.AsSpan(offsetInBlock * channels, take * channels)
                 .CopyTo(destination[(written * channels)..]);

            written += take;
        }

        return written;
    }

    /// <summary>丢弃 <paramref name="frameIndex"/> 之前的所有块。</summary>
    /// <remarks>
    /// 按块对齐：落在同一块内的历史不会被切掉，最多多留不到一块（约 186 毫秒）的内容。
    /// </remarks>
    public void DropBefore(long frameIndex)
    {
        var blockIndex = frameIndex / BlockFrames;
        var drop = blockIndex - _firstBlockIndex;
        if (drop <= 0) return;

        // 不能把还没解码的部分也丢掉。
        drop = Math.Min(drop, _blocks.Count);
        if (drop <= 0) return;

        // 用 RemoveRange 而不是逐个 RemoveAt：丢弃通常成批发生，避免反复搬移整个列表。
        _blocks.RemoveRange(0, (int)drop);
        _firstBlockIndex += drop;
    }

    /// <summary>清空全部内容，回到「什么也没解码」的状态。</summary>
    public void Clear()
    {
        _blocks.Clear();
        _firstBlockIndex = 0;
        _decodedFrames = 0;
    }

    private float[] GetOrCreateBlock(long blockIndex)
    {
        var local = (int)(blockIndex - _firstBlockIndex);

        // 正常路径是顺序追加，while 只会补最后一块那一次；
        // 用它而不是直接 Add 是为了容忍调用方跳过若干块号的情形。
        while (_blocks.Count <= local) _blocks.Add(new float[BlockFrames * Channels]);

        return _blocks[local];
    }
}
