using System.Diagnostics;
using SimpleLyrics.Core.Audio;

namespace SimpleLyrics.Core.Peaks;

/// <summary>一次峰值生成的结果。</summary>
/// <param name="Pyramid">生成好的金字塔（含权威时长）。</param>
/// <param name="Info">文件元信息。</param>
/// <param name="Elapsed">实际耗时，用于在界面上展示首开性能。</param>
public sealed record PeakGenerationResult(PeakPyramid Pyramid, AudioFileInfo Info, TimeSpan Elapsed);

/// <summary>
/// 一趟完整解码，同时产出精确时长与波形峰值金字塔。
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg 全解码速度实测约 1400–2800 倍实时（4 分钟文件 0.08–0.16 秒），
/// 所以 P1 阶段不做持久化缓存也不会有可感知的等待；
/// 等长文件（&gt;30 分钟）的实测数据出来再加 <c>*.slp</c> 旁车缓存即可，接口不必改动。
/// </para>
/// <para>
/// <b>内存策略</b>：全程只保留峰值桶，绝不常驻全量 PCM。
/// 基础级之后的各级都是解码结束后由上一级降采样得到，因此解码热路径里
/// 只剩一个每声道的累加器，没有级联簿记开销。
/// </para>
/// </remarks>
public static class PeakGenerator
{
    /// <summary>基础级每个桶覆盖的帧数。256 帧在 44.1 kHz 下约 5.8 毫秒。</summary>
    public const int BaseBucketFrames = 256;

    /// <summary>每向上一级缩小多少倍。</summary>
    public const int LevelFactor = 4;

    /// <summary>金字塔的最大级数，防止超长文件堆出无意义的极粗级别。</summary>
    public const int MaxLevels = 9;

    /// <summary>
    /// 完整解码一个音频文件并生成峰值金字塔。
    /// </summary>
    /// <param name="path">音频文件路径。</param>
    /// <param name="progress">进度回调，取值 0..1（基于容器时长估算，仅供参考）。</param>
    public static PeakGenerationResult Generate(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        using var decoder = FfmpegDecoder.Open(path, outputChannels: 2);
        var builder = new Builder(decoder.OutputFormat);
        decoder.DecodeTo(builder, progress, cancellationToken);

        stopwatch.Stop();

        var pyramid = builder.Build();

        return new PeakGenerationResult(pyramid, WithExactBitRate(decoder, pyramid), stopwatch.Elapsed);
    }

    /// <summary>
    /// 用<b>精确时长</b>重算一次平均码率，换掉打开文件时那个只能按容器时长估出来的值。
    /// </summary>
    /// <remarks>
    /// 容器声明的时长对 ADTS 之类的裸流是估算的（实测偏差近 30 秒），拿它做分母，
    /// 码率就会跟着偏小一两成——而界面上那个数字正是用户用来对照「这是什么规格的音频」的。
    /// 解码完之后长度有两个独立来源（累计样本数）与它对齐，此时再除一遍才是真值。
    /// </remarks>
    private static AudioFileInfo WithExactBitRate(FfmpegDecoder decoder, PeakPyramid pyramid) =>
        decoder.Info with { BitRate = decoder.ResolveBitRate(pyramid.Duration) };

    /// <summary>解码落点：把交错的浮点流压缩成基础级的桶。</summary>
    private sealed class Builder : IPcmSink
    {
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _bucketFrames = BaseBucketFrames;

        private readonly List<float> _baseMins = [];
        private readonly List<float> _baseMaxs = [];

        private readonly float[] _accumulatedMin;
        private readonly float[] _accumulatedMax;
        private int _accumulatedFrames;

        private long _totalFrames;

        public Builder(PcmFormat format)
        {
            _channels = format.Channels;
            _sampleRate = format.SampleRate;

            _accumulatedMin = new float[_channels];
            _accumulatedMax = new float[_channels];
            ResetAccumulator();
        }

        public void Write(ReadOnlySpan<float> samples, int frameCount)
        {
            var channels = _channels;

            for (var frame = 0; frame < frameCount; frame++)
            {
                var offset = frame * channels;

                for (var channel = 0; channel < channels; channel++)
                {
                    var value = samples[offset + channel];
                    if (value < _accumulatedMin[channel]) _accumulatedMin[channel] = value;
                    if (value > _accumulatedMax[channel]) _accumulatedMax[channel] = value;
                }

                if (++_accumulatedFrames < _bucketFrames) continue;

                for (var channel = 0; channel < channels; channel++)
                {
                    _baseMins.Add(_accumulatedMin[channel]);
                    _baseMaxs.Add(_accumulatedMax[channel]);
                }

                ResetAccumulator();
            }

            _totalFrames += frameCount;
        }

        public PeakPyramid Build()
        {
            // 末尾不足一个桶的样本直接丢弃：强行补齐会把最后几毫秒的波形拉伸成整桶宽，
            // 反而让尾部看起来比实际更响、更长。代价仅仅是丢失不超过 6 毫秒的尾部。
            var levels = new List<PeakLevel>
            {
                new(BaseBucketFrames, _channels, _baseMins.ToArray(), _baseMaxs.ToArray()),
            };

            while (levels.Count < MaxLevels)
            {
                var previous = levels[^1];
                if (previous.Count < LevelFactor * 4) break;
                levels.Add(Downsample(previous, LevelFactor, _channels));
            }

            return new PeakPyramid(_sampleRate, _channels, _totalFrames, levels);
        }

        private void ResetAccumulator()
        {
            Array.Fill(_accumulatedMin, float.MaxValue);
            Array.Fill(_accumulatedMax, float.MinValue);
            _accumulatedFrames = 0;
        }

        /// <summary>把上一级每 <paramref name="factor"/> 个桶合并成一个新桶。</summary>
        private static PeakLevel Downsample(PeakLevel source, int factor, int channels)
        {
            var count = source.Count / factor;
            var mins = new float[count * channels];
            var maxs = new float[count * channels];

            for (var bucket = 0; bucket < count; bucket++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var min = float.MaxValue;
                    var max = float.MinValue;

                    for (var step = 0; step < factor; step++)
                    {
                        var index = (bucket * factor + step) * channels + channel;
                        if (source.Mins[index] < min) min = source.Mins[index];
                        if (source.Maxs[index] > max) max = source.Maxs[index];
                    }

                    mins[bucket * channels + channel] = min;
                    maxs[bucket * channels + channel] = max;
                }
            }

            return new PeakLevel(source.BucketFrames * factor, channels, mins, maxs);
        }
    }
}
