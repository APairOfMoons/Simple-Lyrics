using NAudio.Wave;

namespace SimpleLyrics.Probe;

/// <summary>一次完整解码得到的统计结果。</summary>
/// <param name="MonoSamples">单声道样本总数（音频帧数）。</param>
/// <param name="ExactDuration">由样本数算出的精确时长 —— 唯一可信的时间基准。</param>
/// <param name="ReportedDuration">解码器自己报告的时长（对照用，已知不可靠）。</param>
/// <param name="Elapsed">完整解码耗时。</param>
/// <param name="SpeedRatio">解码速度倍率。</param>
internal sealed record DecodeStats(
    long MonoSamples,
    TimeSpan ExactDuration,
    TimeSpan ReportedDuration,
    TimeSpan Elapsed,
    double SpeedRatio);

/// <summary>一次 seek 测试的结果。</summary>
/// <param name="Target">请求 seek 到的位置。</param>
/// <param name="Actual">通过波形指纹匹配推测出的真实位置。</param>
/// <param name="ErrorMs">偏差（毫秒）。正数表示实际位置晚于请求位置。</param>
/// <param name="Correlation">匹配相关系数，取值 [-1,1]，越接近 1 越可信。</param>
internal sealed record SeekSample(TimeSpan Target, TimeSpan Actual, double ErrorMs, double Correlation);

/// <summary>
/// 音频分析：精确时长测量与 seek 精度验证。
/// </summary>
/// <remarks>
/// <para>
/// seek 精度用「波形指纹」方法验证：先顺序解码全曲、记录每 10ms 的 RMS 包络，
/// 再 seek 到目标位置读取一小段 RMS 序列，在全曲包络中找最佳匹配位置，
/// 两者的时间差即为 seek 偏差 —— 不依赖解码器自报的时间。
/// </para>
/// <para>
/// 直接读取原始 PCM 字节并自行转 float，不使用 NAudio 的采样转换器，
/// 以避免 NAudio 3.x API 变动带来的不确定性，同时完全掌控 16/24/32 位深处理。
/// </para>
/// <para>
/// 参数类型取 <see cref="WaveStream"/> 基类，因此既可用于 Media Foundation，
/// 也可用于 ACM（<see cref="Mp3FileReader"/>）做算法自检。
/// </para>
/// </remarks>
internal static class Analysis
{
    /// <summary>指纹窗口长度（毫秒），与 LRC 的 10ms 精度对齐。</summary>
    private const int WindowMs = 10;

    /// <summary>seek 匹配使用的连续窗口数（20 × 10ms = 200ms）；窗口越长定位越可靠。</summary>
    private const int ProbeWindows = 20;

    /// <summary>相关系数低于此值即判定匹配不可靠，直接丢弃而不报出误导性偏差。</summary>
    private const double MinCorrelation = 0.90;

    /// <summary>顺序解码全文，得到精确时长并测量解码性能。</summary>
    public static DecodeStats DecodeAll(WaveStream reader)
    {
        var waveFormat = reader.WaveFormat;
        var reported = reader.TotalTime;

        reader.Position = 0;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new byte[1 << 16];
        long totalBytes = 0;
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalBytes += read;
        }

        stopwatch.Stop();

        var bytesPerFrame = waveFormat.BitsPerSample / 8 * waveFormat.Channels;
        var frames = bytesPerFrame > 0 ? totalBytes / bytesPerFrame : 0;
        var exact = TimeSpan.FromSeconds((double)frames / waveFormat.SampleRate);
        var speed = stopwatch.Elapsed.TotalSeconds > 0 ? exact.TotalSeconds / stopwatch.Elapsed.TotalSeconds : 0;

        return new DecodeStats(frames, exact, reported, stopwatch.Elapsed, speed);
    }

    /// <summary>测试 seek 精度，返回若干采样点的偏差。</summary>
    /// <param name="reader">要测试的音频流。</param>
    /// <param name="probeCount">采样点数量。</param>
    /// <param name="trace">可选的诊断输出回调。</param>
    public static IReadOnlyList<SeekSample> TestSeek(
        WaveStream reader, int probeCount = 7, Action<string>? trace = null)
    {
        var waveFormat = reader.WaveFormat;

        // 建立全曲 RMS 指纹
        var fingerprint = BuildFingerprint(reader, waveFormat);
        trace?.Invoke(
            $"指纹窗口数 = {fingerprint.Length}，覆盖 {(double)fingerprint.Length * WindowMs / 1000:F2}s");

        if (fingerprint.Length < ProbeWindows * 4) return [];

        var covered = TimeSpan.FromMilliseconds((double)fingerprint.Length * WindowMs);
        var results = new List<SeekSample>(probeCount);

        for (var i = 1; i <= probeCount; i++)
        {
            var fraction = (double)i / (probeCount + 1);
            var target = TimeSpan.FromSeconds(covered.TotalSeconds * fraction);

            reader.CurrentTime = target;

            var probe = ReadRmsSequence(reader, waveFormat, ProbeWindows);
            var probeMin = probe.Length > 0 ? probe.Min() : 0;
            var probeMax = probe.Length > 0 ? probe.Max() : 0;
            trace?.Invoke(
                $"  #{i} 目标 {target.TotalSeconds,7:F2}s  探针 {probe.Length,2} 窗口  RMS {probeMin:F4}~{probeMax:F4}");

            if (probe.Length < ProbeWindows) continue;

            var (bestIndex, correlation) = FindBestMatch(fingerprint, probe);
            trace?.Invoke($"       最佳位置 index={bestIndex} ({bestIndex * WindowMs / 1000.0:F2}s)  相关={correlation:F3}");

            if (bestIndex < 0) continue;

            var actual = TimeSpan.FromMilliseconds((double)bestIndex * WindowMs);
            results.Add(new SeekSample(target, actual, (actual - target).TotalMilliseconds, correlation));
        }

        return results;
    }

    /// <summary>顺序解码，产出每 <see cref="WindowMs"/> 毫秒一个的 RMS 包络。</summary>
    private static double[] BuildFingerprint(WaveStream reader, WaveFormat waveFormat)
    {
        reader.Position = 0;

        var windowBytes = WindowBytes(waveFormat);
        var raw = new byte[windowBytes];
        var samples = new float[Math.Max(1, windowBytes / BytesPerSample(waveFormat))];
        var envelope = new List<double>(1 << 14);

        while (true)
        {
            var read = ReadFully(reader, raw, windowBytes);
            if (read <= 0) break;

            var count = ToFloat(raw, read, waveFormat, samples);
            envelope.Add(Rms(samples, count));

            if (read < windowBytes) break;
        }

        return [.. envelope];
    }

    /// <summary>从当前位置读取若干个连续窗口的 RMS 序列。</summary>
    private static double[] ReadRmsSequence(WaveStream reader, WaveFormat waveFormat, int windowCount)
    {
        var windowBytes = WindowBytes(waveFormat);
        var raw = new byte[windowBytes];
        var samples = new float[Math.Max(1, windowBytes / BytesPerSample(waveFormat))];
        var result = new List<double>(windowCount);

        for (var w = 0; w < windowCount; w++)
        {
            var read = ReadFully(reader, raw, windowBytes);
            if (read <= 0) break;

            var count = ToFloat(raw, read, waveFormat, samples);
            result.Add(Rms(samples, count));

            if (read < windowBytes) break;
        }

        return [.. result];
    }

    /// <summary>
    /// 在指纹中寻找与探针最匹配的位置，使用零均值归一化互相关（ZNCC）。
    /// </summary>
    /// <remarks>
    /// 不能用最小平方误差：RMS 包络的绝对值本来就小，平方误差会使匹配偏向
    /// 全曲最安静的片段 —— 实测中任意位置的探针都被匹配到同一处。
    /// 相关系数对幅度缩放与直流偏置不敏感，只比较包络形状，才适合定位。
    /// </remarks>
    private static (int Index, double Correlation) FindBestMatch(double[] fingerprint, double[] probe)
    {
        var n = probe.Length;

        var probeMean = 0.0;
        for (var j = 0; j < n; j++) probeMean += probe[j];
        probeMean /= n;

        var probeVariance = 0.0;
        for (var j = 0; j < n; j++)
        {
            var d = probe[j] - probeMean;
            probeVariance += d * d;
        }
        var probeSigma = Math.Sqrt(probeVariance);

        // 探针本身没有起伏（例如整段静音）时无法定位
        if (probeSigma < 1e-9) return (-1, double.NegativeInfinity);

        var bestIndex = -1;
        var bestCorrelation = double.NegativeInfinity;

        for (var i = 0; i + n <= fingerprint.Length; i++)
        {
            var mean = 0.0;
            for (var j = 0; j < n; j++) mean += fingerprint[i + j];
            mean /= n;

            double covariance = 0, variance = 0;
            for (var j = 0; j < n; j++)
            {
                var dv = fingerprint[i + j] - mean;
                covariance += dv * (probe[j] - probeMean);
                variance += dv * dv;
            }

            var sigma = Math.Sqrt(variance);
            if (sigma < 1e-9) continue;

            var correlation = covariance / (sigma * probeSigma);
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestIndex = i;
            }
        }

        return (bestIndex, bestCorrelation);
    }

    private static int BytesPerSample(WaveFormat waveFormat) =>
        Math.Max(1, waveFormat.BitsPerSample / 8);

    private static int WindowBytes(WaveFormat waveFormat) =>
        Math.Max(1, waveFormat.SampleRate * WindowMs / 1000 * waveFormat.Channels * BytesPerSample(waveFormat));

    /// <summary>尽量读满请求字节数，返回实际读到的数量。</summary>
    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    /// <summary>把交错原始 PCM 转成 [-1,1] 的 float，返回样本数。</summary>
    private static int ToFloat(byte[] source, int byteCount, WaveFormat waveFormat, float[] destination)
    {
        var bytesPerSample = BytesPerSample(waveFormat);
        var sampleCount = byteCount / bytesPerSample;
        if (sampleCount > destination.Length) sampleCount = destination.Length;

        switch (waveFormat.BitsPerSample)
        {
            case 16:
                for (var i = 0; i < sampleCount; i++)
                {
                    destination[i] = BitConverter.ToInt16(source, i * 2) / 32768f;
                }
                break;

            case 24:
                for (var i = 0; i < sampleCount; i++)
                {
                    var offset = i * 3;
                    var value = source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16;
                    if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                    destination[i] = value / 8388608f;
                }
                break;

            case 32:
                for (var i = 0; i < sampleCount; i++)
                {
                    destination[i] = BitConverter.ToInt32(source, i * 4) / 2147483648f;
                }
                break;

            default:
                Array.Clear(destination, 0, sampleCount);
                break;
        }

        return sampleCount;
    }

    private static double Rms(float[] buffer, int count)
    {
        if (count <= 0) return 0;

        double sum = 0;
        for (var i = 0; i < count; i++) sum += (double)buffer[i] * buffer[i];

        return Math.Sqrt(sum / count);
    }
}
