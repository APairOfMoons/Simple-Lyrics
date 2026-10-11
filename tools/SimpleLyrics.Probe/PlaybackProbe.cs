using SimpleLyrics.Core.Audio;
using SimpleLyrics.Core.Peaks;

namespace SimpleLyrics.Probe;

/// <summary>
/// 播放链路的自动化验证：分块存储、增量解码、整曲缓存稳定性。
/// </summary>
/// <remarks>
/// 沙箱里音频设备被禁用，所以这里<b>不测出声</b>——那部分只能由管理员在 Visual Studio 里听。
/// 但播放链路里真正容易错的是数据：把一趟连续解码切成许多小块之后，
/// 样本有没有少、有没有重、重采样器跨块的状态对不对。这些都能在这里逐位核对。
/// </remarks>
internal static class PlaybackProbe
{
    /// <summary>FNV-1a 64 位。用于逐位比对，位模式不同就一定算出不同的值。</summary>
    private const ulong FnvOffset = 14695981039346656037UL;

    private const ulong FnvPrime = 1099511628211UL;

    private static readonly string[] Formats = ["test.mp3", "test.flac", "test.aac", "test.ogg"];

    public static void Run(string root)
    {
        Console.WriteLine("---- 播放缓存 ----");

        var failures = VerifyBlockStore();

        foreach (var name in Formats)
        {
            var path = TestAudio.Resolve(root, name);
            if (path is null || !File.Exists(path))
            {
                Console.WriteLine($"  {name,-12} 不存在");
                continue;
            }

            failures += VerifyWholeTrackCaching(name, path);
        }

        failures += VerifyNegativeQuery(root);

        Console.WriteLine(failures == 0
            ? "  播放缓存全部通过"
            : $"  播放缓存有 {failures} 项失败");
        Console.WriteLine();
    }

    // ------------------------------------------------------------ 分块存储

    private static int VerifyBlockStore()
    {
        const int channels = 2;
        var store = new PcmBlockStore(44100, channels);

        // 刻意取 3 块多一点：最后一块不满，且读取会跨过块边界。
        var frames = PcmBlockStore.BlockFrames * 3 + 1234;
        var source = new float[frames * channels];
        for (var i = 0; i < source.Length; i++) source[i] = i * 1e-4f;

        store.Append(source, frames);

        if (store.EndFrame != frames) return Fail("分块存储", $"帧数 {store.EndFrame} ≠ {frames}");
        if (store.FirstFrame != 0) return Fail("分块存储", "起始帧不为 0");

        // 用刻意不对齐块大小的长度分多次读回，逼出跨块拼接。
        var readBack = new float[frames * channels];
        var total = 0;
        while (total < frames)
        {
            var want = Math.Min(PcmBlockStore.BlockFrames + 100, frames - total);
            var got = store.Read(total, readBack.AsSpan(total * channels), want);
            if (got <= 0) break;
            total += (int)got;
        }

        if (total != frames) return Fail("分块存储", $"读回 {total} 帧 ≠ {frames} 帧");

        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != readBack[i]) return Fail("分块存储", $"第 {i} 个样本读回后不一致");
        }

        // 丢掉头部两块，再验证区间与内容都跟着平移了。
        store.DropBefore(PcmBlockStore.BlockFrames * 2);

        var expectedFirst = (long)PcmBlockStore.BlockFrames * 2;
        if (store.FirstFrame != expectedFirst)
            return Fail("分块存储", $"丢弃后起始帧 {store.FirstFrame} ≠ {expectedFirst}");

        var probe = new float[16 * channels];
        var read = store.Read(store.FirstFrame, probe, 16);
        if (read != 16) return Fail("分块存储", "丢弃后读取失败");

        var offset = store.FirstFrame * channels;
        for (var i = 0; i < probe.Length; i++)
        {
            if (probe[i] != source[offset + i]) return Fail("分块存储", "丢弃后读到的是错位内容");
        }

        Console.WriteLine($"  {"分块存储",-12} OK     {frames:N0} 帧 · 跨块读写与头部丢弃均一致");
        return 0;
    }

    // ------------------------------------------------------------ 整曲缓存

    /// <summary>
    /// 核对整曲缓存：分块增量解码逐位正确，且缓存铺满之后反复改播放位置不会清空重解。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 前半段是整条播放链路上最关键的一条断言。解码器被反复地「解一小块就返回」，
    /// 重采样器却是同一个实例——只要块边界处理有丝毫偏差（少一帧、重一帧、
    /// 或者重采样状态被意外重置），哈希立刻就会不同。
    /// </para>
    /// <para>
    /// 后半段针对的是另一种浪费：缓存明明装得下整曲，却每次挪播放头都重头解码。
    /// 短素材应当一次铺满并从此保持不动，之后任何跳转都只是数组索引而已。
    /// </para>
    /// </remarks>
    private static int VerifyWholeTrackCaching(string name, string path)
    {
        try
        {
            var pyramid = PeakGenerator.Generate(path).Pyramid;
            var totalFrames = pyramid.TotalFrames;

            using var decoder = FfmpegDecoder.Open(path, 2);
            var expected = new HashCollector();
            decoder.DecodeTo(expected);

            if (expected.Frames != totalFrames)
                return Fail(name, $"一次性解码 {expected.Frames:N0} 帧 ≠ 权威 {totalFrames:N0} 帧");

            using var player = new AudioPlayer(path, totalFrames);
            player.Prepare(0);

            if (!WaitFor(player, () => player.CacheEndSeconds >= player.DurationSeconds, TimeSpan.FromSeconds(180)))
                return Fail(name, "缓存未能在超时内铺满整曲");

            var actual = HashCacheRange(player, 0, totalFrames);

            if (actual.Frames != totalFrames)
                return Fail(name, $"缓存只有 {actual.Frames:N0} 帧，权威是 {totalFrames:N0} 帧");

            if (actual.Hash != expected.Hash)
                return Fail(name, "分块解码的样本与一次性解码不一致");

            // 音频之外恒为可播：那里是天然静音，不需要缓存支撑。
            // 若这里返回 false，播放按钮会被一个永远无法满足的条件锁死——
            // 用户把指示条拖到曲首之前就再也放不出声音了。
            if (!player.Covers(-10)) return Fail(name, "曲首之前的时刻被判为不可播");
            if (!player.Covers(player.DurationSeconds + 10)) return Fail(name, "曲尾之后的时刻被判为不可播");
            if (!player.Covers(player.DurationSeconds * 0.5)) return Fail(name, "曲中时刻被判为不可播");

            // 静音区间：曲首之前一直静音，曲尾之后静音到一个整曲时长为止。
            if (AudioPlayer.SilenceFramesAt(-44100, 1024, totalFrames) != 1024)
                return Fail(name, "曲首之前没有产出满额静音");

            if (AudioPlayer.SilenceFramesAt(-100, 1024, totalFrames) != 100)
                return Fail(name, "曲首之前的静音没有在音频边界处截断");

            if (AudioPlayer.SilenceFramesAt(0, 1024, totalFrames) != 0)
                return Fail(name, "音频之内被误判为静音");

            if (AudioPlayer.SilenceFramesAt(totalFrames / 2, 1024, totalFrames) != 0)
                return Fail(name, "音频之内（中段）被误判为静音");

            if (AudioPlayer.SilenceFramesAt(totalFrames + 44100, 1024, totalFrames) != 1024)
                return Fail(name, "曲尾之后没有产出满额静音");

            if (AudioPlayer.SilenceFramesAt(totalFrames * 2 - 100, 1024, totalFrames) != 100)
                return Fail(name, "尾部静音没有在超限上界处截断");

            if (AudioPlayer.SilenceFramesAt(totalFrames * 2, 1024, totalFrames) != 0)
                return Fail(name, "超出尾部静音上界后仍在产出静音");

            // 铺满之后反复挪播放头，缓存区间必须纹丝不动。
            var start = player.CacheStartSeconds;
            var end = player.CacheEndSeconds;
            double[] fractions = [0.9, 0.1, 0.5, 0.0, 0.99, 0.3];

            foreach (var fraction in fractions)
            {
                player.Prepare(player.DurationSeconds * fraction);
                Thread.Sleep(25);

                if (player.CacheStartSeconds != start || player.CacheEndSeconds != end)
                {
                    return Fail($"{name}/跳转",
                        $"挪到 {fraction * 100:F0}% 后缓存区间从 {start:F2}–{end:F2}s 变成 "
                        + $"{player.CacheStartSeconds:F2}–{player.CacheEndSeconds:F2}s，说明被清空重解了");
                }
            }

            Console.WriteLine(
                $"  {name,-12} OK     {totalFrames:N0} 帧逐位一致 · 整曲 {end:F2}s 常驻，"
                + $"{fractions.Length} 次跳转均未重新解码");

            return 0;
        }
        catch (Exception ex)
        {
            return Fail(name, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ 负区间

    /// <summary>
    /// 验证超限滚动时「音频开始之前」的那些行不会被画成波形。
    /// </summary>
    /// <remarks>
    /// 这里曾经有个真实的缺陷：查询区间整体落在负区时，起点会被钳到 0，
    /// 于是一段完全在音频之前的查询退化成「查询 0 秒附近」，
    /// 把开头那一下的波形错误地铺满整个上方空白区。
    /// </remarks>
    private static int VerifyNegativeQuery(string root)
    {
        var path = TestAudio.Resolve(root, "test.mp3");
        if (path is null || !File.Exists(path)) return 0;

        try
        {
            var pyramid = PeakGenerator.Generate(path).Pyramid;

            var (min, max) = pyramid.Query(0, -10, -5);
            if (min != 0f || max != 0f)
                return Fail("负区间", $"音频之前的查询返回 {min}..{max}，应为静音");

            const int rows = 64;
            var envelope = new float[pyramid.Channels][];
            for (var channel = 0; channel < pyramid.Channels; channel++) envelope[channel] = new float[rows];

            pyramid.FillEnvelope(-10, 0.1, envelope);

            foreach (var channel in envelope)
            {
                foreach (var value in channel)
                {
                    if (value != 0f) return Fail("负区间", "超限区域画出了波形");
                }
            }

            Console.WriteLine($"  {"负区间",-12} OK     音频开始之前的查询与包络均为静音");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail("负区间", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ 工具

    private static bool WaitFor(AudioPlayer player, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            // 解码线程一旦出错会记在这里；把它抛出来比等到超时更有信息量。
            if (player.Failure is not null) throw player.Failure;
            if (condition()) return true;

            Thread.Sleep(15);
        }

        return false;
    }

    private static (ulong Hash, long Frames) HashCacheRange(AudioPlayer player, long startFrame, long frameCount)
    {
        var channels = player.Format.Channels;
        var buffer = new float[PcmBlockStore.BlockFrames * channels];

        var hash = FnvOffset;
        var frame = startFrame;
        long remaining = frameCount;
        long total = 0;

        while (remaining > 0)
        {
            var want = (int)Math.Min(PcmBlockStore.BlockFrames, remaining);
            var read = player.ReadCached(frame, buffer, want);
            if (read <= 0) break;

            for (var i = 0; i < read * channels; i++)
            {
                hash = (hash ^ BitConverter.SingleToUInt32Bits(buffer[i])) * FnvPrime;
            }

            frame += read;
            remaining -= read;
            total += read;
        }

        return (hash, total);
    }

    private static int Fail(string subject, string message)
    {
        Console.WriteLine($"  {subject,-14} 失败   {message}");
        return 1;
    }

    /// <summary>逐样本累加 FNV-1a。浮点值按其位模式参与运算，-0.0 与 0.0 也会被区分开。</summary>
    private sealed class HashCollector : IPcmSink
    {
        public ulong Hash { get; private set; } = FnvOffset;

        public long Frames { get; private set; }

        public void Write(ReadOnlySpan<float> samples, int frameCount)
        {
            var hash = Hash;

            foreach (var value in samples)
            {
                hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * FnvPrime;
            }

            Hash = hash;
            Frames += frameCount;
        }
    }
}
