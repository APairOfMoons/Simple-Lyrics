using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using SimpleLyrics.Controls;
using SimpleLyrics.Core.Peaks;

namespace SimpleLyrics.Preview;

/// <summary>
/// 把 <see cref="WaveformView"/> 离屏渲染成 PNG。
/// </summary>
/// <remarks>
/// <para>
/// 自绘控件的正确性分两层：<b>数据正确</b>由 <c>PeakProbe</c> 与 <c>PlaybackProbe</c> 自动验证，
/// <b>视觉正确</b>过去只能靠启动 GUI 用肉眼判断。本工具补上后者——
/// 不需要窗口系统，直接光栅化到 <see cref="RenderTargetBitmap"/>，
/// 于是外观的每次微调都能在几秒内看到结果，也能在评审时附上实际截图。
/// </para>
/// <para>用法：<c>SimpleLyrics.Preview &lt;音频文件&gt; &lt;输出目录&gt;</c></para>
/// </remarks>
internal static class Program
{
    /// <summary>与主窗口左栏一致的尺寸。</summary>
    private const int Width = 330;
    private const int Height = 736;

    [STAThread]
    private static int Main(string[] args)
    {
        var (audioPath, outputDirectory) = ResolveArguments(args);

        if (!File.Exists(audioPath))
        {
            Console.WriteLine($"找不到音频文件：{audioPath}");
            Console.WriteLine("用法：SimpleLyrics.Preview [音频文件] [输出目录]（都省略则用仓库里的 P0 素材）");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);

        // 先把数据准备好，这一步与 Avalonia 无关。
        var result = PeakGenerator.Generate(audioPath);
        var pyramid = result.Pyramid;

        Console.WriteLine($"素材     : {Path.GetFileName(audioPath)}");
        Console.WriteLine($"时长     : {pyramid.Duration:hh\\:mm\\:ss\\.fff}   帧数 {pyramid.TotalFrames:N0}");
        Console.WriteLine($"金字塔   : {pyramid.Levels.Count} 级   内存 {pyramid.ApproximateBytes / 1024.0:F0} KB");
        Console.WriteLine($"解码耗时 : {result.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine();

        // 只做平台初始化，不启动消息循环、不建窗口。
        // 这里刻意用最小外壳而不是 SimpleLyrics.App 里的 App：
        // 一来工具不该依赖整个应用，二来引用 WinExe 项目会让 MSBuild 在项目引用协商阶段静默失败。
        AppBuilder.Configure<PreviewShell>()
            .UsePlatformDetect()
            .WithInterFont()
            .SetupWithoutStarting();

        var size = new Size(Width, Height);
        // 演示缓外遮罩用的假缓存区间：曲首 40 秒与末尾 75 秒留在缓存之外。
        var duration = pyramid.DurationSeconds;

        // ① 整曲适配：打开文件后立刻看到的画面。曲首对齐指示器，上方那段空白是曲前。
        Render(outputDirectory, "1-整曲适配", pyramid, size);

        // ② 放大到每屏约 18 秒：验证细节视图与刻度密度。
        Render(outputDirectory, "2-放大-每屏18秒", pyramid, size,
            pixelsPerSecond: Height / 18.0, scrollSeconds: 60);

        // ③ 请求一个远超数据精度的缩放（5000 像素/秒），验证上限被钳制在
        //    「金字塔最细的一个桶 ≈ 一个像素」，而不是把同一个桶拉成横条纹。
        Render(outputDirectory, "3-最大放大-已钳制", pyramid, size,
            pixelsPerSecond: 5000, scrollSeconds: 30);

        // ④ 缓外遮罩：每屏 200 秒，一口气看到「曲前无内容 / 缓外 / 已缓存 / 缓外」四段。
        Render(outputDirectory, "4-缓存未覆盖全曲", pyramid, size,
            pixelsPerSecond: Height / 200.0, scrollSeconds: 0,
            cacheStart: 40, cacheEnd: duration - 75);

        // ⑤ 超限滚动：顶端时刻是负数，刻度带负号，上方整段是曲前空白。
        Render(outputDirectory, "5-超限滚动-曲首之前", pyramid, size,
            pixelsPerSecond: Height / 60.0, scrollSeconds: -25,
            cacheStart: 0, cacheEnd: duration);

        // ⑥ 播放中正在拖动定位：内容已跟手挪开，指示条实时显示落点，顶部有说明行。
        RenderDragInProgress(outputDirectory, "6-拖动定位中", pyramid, size);

        MeasureRenderCost(pyramid, size);

        // 包络本身是稳的：这个测量是为了在怀疑「采样粒度导致闪烁」时能一眼排除它。
        MeasureEnvelopeStability(pyramid, size);
        MeasureFollowSmoothness(pyramid, size);

        // 波带在非整数显示缩放比下是否还保持恒定厚度。这是「宽线条」方案的验收标准。
        MeasureBandThickness(pyramid, size);

        // 首尾两条带本来就有一半在控件之外，检查的是它们没有被画到控件外面去。
        MeasureOverflow(pyramid, size);

        // 两条交互约定：缩放恒以指示条为锚；播放中复位要连播放位置一起搬。
        MeasureZoomAnchor(pyramid, size);
        MeasureFitWhilePlaying(pyramid, size);

        // 拖动定位在播放中与未播放时的分野。
        MeasureWaveformDrag(pyramid, size);

        return 0;
    }

    /// <summary>
    /// 检查播放跟随是否<b>每帧</b>都在推进滚动位置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个测量必须走 <see cref="WaveformView.PlayheadSeconds"/> 而不是直接设
    /// <c>ScrollSeconds</c>——跟随逻辑、以及任何加在它上面的节流，都只在前者这条路径上生效。
    /// 直接设滚动位置会把待测的东西整个绕过去。
    /// </para>
    /// <para>
    /// 为什么要数这个：低倍率下播放一帧只前进约 0.05 像素，若有「不足半个像素就不动」这类
    /// 节流，滚动就变成十几帧才跳一次的低频顿挫。低频跳跃被视觉判读为闪烁，
    /// 反而比每帧小幅推进更刺眼——实测数据支持这一点，所以这类节流是净损失。
    /// </para>
    /// </remarks>
    private static void MeasureFollowSmoothness(PeakPyramid pyramid, Size size)
    {
        var duration = pyramid.DurationSeconds;
        if (duration <= 0) return;

        (string Label, double PixelsPerSecond)[] cases =
        [
            ("整曲适配（每屏一曲）", size.Height / duration),
            ("每屏 60 秒", size.Height / 60.0),
            ("每屏 18 秒", size.Height / 18.0),
            ("最大放大（已钳制）", MaximumZoom(pyramid, size)),
        ];

        Console.WriteLine("---- 播放跟随时滚动位置的推进 ----");
        Console.WriteLine("  场景                        60 帧中推进    每帧位移");

        const int frames = 60;
        const double secondsPerFrame = 1.0 / 60.0;

        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);

        foreach (var (label, pixelsPerSecond) in cases)
        {
            view.PixelsPerSecond = pixelsPerSecond;

            // 从曲中开始，避开两端的钳制区间。
            var start = duration * 0.4;
            view.PlayheadSeconds = start;

            var advanced = 0;
            var previous = view.ScrollSeconds;

            for (var i = 0; i < frames; i++)
            {
                view.PlayheadSeconds = start + (i + 1) * secondsPerFrame;

                if (view.ScrollSeconds != previous) advanced++;
                previous = view.ScrollSeconds;
            }

            var perFrame = secondsPerFrame * pixelsPerSecond;

            Console.WriteLine(
                $"  {label,-26} {advanced,6}/{frames} 帧  {perFrame,9:F3} px");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 量化 <see cref="PeakPyramid.FillEnvelope"/> 在相邻帧之间的跳变程度。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>注意：这条路径已不由波形控件使用。</b>渲染改按「波带」聚合（每条带等于整数个基础桶，
    /// 见 <c>WaveformView.FillBands</c>），不经过 <c>Query</c>，也就没有这里量到的外扩跳变。
    /// 留着它是因为它记录了「按屏幕像素行取数」为什么会抖——若日后有人想把渲染改回去，
    /// 这组数字就是反对意见的依据。探针仍用它验证金字塔本身。
    /// </para>
    /// <para>
    /// 播放时每帧只前进十几毫秒，画面本该是平滑的整体平移。
    /// 但取包络时每行都要把时间区间吸附到桶边界上（<c>floor</c> / <c>ceil</c>），
    /// 当一行跨越多个桶时，桶集合在各帧之间会独立地增减，包络内容因此<b>逐行跳变</b>。
    /// 这个测量把「跳得厉害不厉害」变成一个比例：相邻两帧之间变化超过满量程 5% 的行占比。
    /// </para>
    /// </remarks>
    private static void MeasureEnvelopeStability(PeakPyramid pyramid, Size size)
    {
        var duration = pyramid.DurationSeconds;
        if (duration <= 0) return;

        var rows = (int)Math.Ceiling(size.Height);
        var channels = pyramid.Channels;

        var current = new float[channels][];
        var previous = new float[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            current[channel] = new float[rows];
            previous[channel] = new float[rows];
        }

        var maximum = MaximumZoom(pyramid, size);

        (string Label, double PixelsPerSecond)[] cases =
        [
            ("整曲适配（每屏一曲）", size.Height / duration),
            ("每屏 60 秒", size.Height / 60.0),
            ("每屏 18 秒", size.Height / 18.0),
            ("最大放大（已钳制）", maximum),
        ];

        Console.WriteLine("---- 播放滚动时包络的帧间跳变 ----");
        Console.WriteLine("  场景                        跳变行占比     平均跳变    每行覆盖");

        const int frames = 60;
        const double secondsPerFrame = 1.0 / 60.0;

        foreach (var (label, pixelsPerSecond) in cases)
        {
            var secondsPerRow = 1.0 / pixelsPerSecond;
            var scroll = duration * 0.3;

            long jumped = 0;
            long total = 0;
            double sum = 0;

            pyramid.FillEnvelope(scroll, secondsPerRow, previous);

            for (var i = 0; i < frames; i++)
            {
                scroll += secondsPerFrame;
                pyramid.FillEnvelope(scroll, secondsPerRow, current);

                for (var channel = 0; channel < channels; channel++)
                {
                    var now = current[channel];
                    var before = previous[channel];

                    for (var row = 0; row < rows; row++)
                    {
                        var delta = Math.Abs(now[row] - before[row]);
                        sum += delta;
                        if (delta > 0.05) jumped++;
                        total++;
                    }
                }

                (previous, current) = (current, previous);
            }

            var ratio = total > 0 ? (double)jumped / total : 0;
            var average = total > 0 ? sum / total : 0;

            Console.WriteLine(
                $"  {label,-26} {ratio,9:P1}  {average,11:F4}  {secondsPerRow * 1000,8:F1} ms");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 模拟播放时的连续滚动，测出每帧的渲染成本。
    /// </summary>
    /// <remarks>
    /// 播放时波形每帧都要往前滚，若单帧成本越过 16 毫秒这一档就会丢帧，
    /// 表现正是「抽动」。有了这个数字，就不必再靠肉眼去猜瓶颈在包络重算还是在绘制。
    /// </remarks>
    private static void MeasureRenderCost(PeakPyramid pyramid, Size size)
    {
        var duration = pyramid.DurationSeconds;
        if (duration <= 0) return;

        (string Label, double PixelsPerSecond)[] cases =
        [
            ("整曲适配（每屏一曲）", size.Height / duration),
            ("每屏 18 秒", size.Height / 18.0),
            ("最大放大（已钳制）", MaximumZoom(pyramid, size)),
        ];

        Console.WriteLine("---- 播放时每帧的渲染成本 ----");
        Console.WriteLine("  场景                        每帧耗时    推算帧率");

        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)size.Width, (int)size.Height),
            new Vector(96, 96));

        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);

        // 先光栅化一次，把「载入后自动适配」走完。
        bitmap.Render(view);

        const int frames = 60;
        const double secondsPerFrame = 1.0 / 60.0;

        foreach (var (label, pixelsPerSecond) in cases)
        {
            view.PixelsPerSecond = pixelsPerSecond;
            view.ScrollSeconds = duration * 0.3;

            // 热身，避开 JIT 与首次分配的影响。
            for (var i = 0; i < 5; i++)
            {
                view.ScrollSeconds += secondsPerFrame;
                bitmap.Render(view);
            }

            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < frames; i++)
            {
                // 模拟播放：每帧把顶端时刻往前推一点点，包络缓存因此必然失效。
                view.ScrollSeconds += secondsPerFrame;
                bitmap.Render(view);
            }

            stopwatch.Stop();

            var perFrame = stopwatch.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine(
                $"  {label,-26} {perFrame,7:F2} ms  {1000.0 / Math.Max(perFrame, 0.001),7:F0} fps");
        }

        Console.WriteLine();
    }

    private static void Render(
        string outputDirectory,
        string label,
        PeakPyramid pyramid,
        Size size,
        double? pixelsPerSecond = null,
        double? scrollSeconds = null,
        double? cacheStart = null,
        double? cacheEnd = null)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);

        // 首次光栅化会把「载入后自动适配整曲」这一步走完；
        // 先让它发生，之后设置的缩放与滚动才不会被自动适配覆盖。
        Rasterize(view, size, null);

        // 未指定缓存区间时留成 NaN，控件就不画遮罩——这与「整曲都在缓存里」是两种表达。
        view.CacheStartSeconds = cacheStart ?? double.NaN;
        view.CacheEndSeconds = cacheEnd ?? double.NaN;

        if (pixelsPerSecond is { } zoom)
        {
            view.PixelsPerSecond = zoom;
            view.ScrollSeconds = scrollSeconds ?? 0;
        }

        view.InvalidateVisual();

        var name = $"{label}.png";
        Rasterize(view, size, Path.Combine(outputDirectory, name));

        var cache = double.IsNaN(view.CacheStartSeconds)
            ? "无"
            : $"{view.CacheStartSeconds:F0}–{view.CacheEndSeconds:F0}s";

        Console.WriteLine(
            $"已输出 {name,-28} 每秒像素 {view.PixelsPerSecond,7:F2}   "
            + $"顶端 {view.ScrollSeconds,8:F2}s   缓存 {cache}");
    }

    /// <summary>
    /// 渲染一张「播放中正在拖动定位」的画面。
    /// </summary>
    /// <remarks>
    /// 这一帧在真实操作里只存在几百毫秒，但它承载了两件用户看不见的事：
    /// 拖动已经生效（内容挪开了、指示条的落点在实时变），以及声音<b>还没</b>跟过去。
    /// 后者尤其需要那行字，否则用户会以为拖动失灵。
    /// </remarks>
    private static void RenderDragInProgress(
        string outputDirectory,
        string label,
        PeakPyramid pyramid,
        Size size)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);

        // 先走完「载入后自动适配」，之后的设置才不会被它覆盖。
        using (RasterizeToBitmap(view, size, 1.0)) { }

        view.PixelsPerSecond = size.Height / 18.0;
        view.ScrollSeconds = 60;
        view.PlayheadSeconds = 60;
        view.IsPlaying = true;

        const double dragPixels = 32;
        view.BeginWaveformDrag(300);
        view.UpdateWaveformDrag(300 - dragPixels);

        var name = $"{label}.png";

        using (var bitmap = RasterizeToBitmap(view, size, 1.0)) bitmap.Save(Path.Combine(outputDirectory, name));

        Console.WriteLine(
            $"已输出 {name,-28} 拖动位移 {dragPixels:F0} 像素 = "
            + $"{dragPixels * 1000 / view.PixelsPerSecond:F0} ms（自 {60 + 0.4 * 18:F3}s 挪到 "
            + $"{view.PlayStartSeconds:F3}s）");

        view.EndWaveformDrag();
    }

    private static void Layout(Layoutable view, Size size)
    {
        view.Measure(size);
        view.Arrange(new Rect(size));
    }

    private static void Rasterize(WaveformView view, Size size, string? path)
    {
        using var bitmap = RasterizeToBitmap(view, size, 1.0);
        if (path is not null) bitmap.Save(path);
    }

    private static RenderTargetBitmap Rasterize(Layoutable visual, Size size) =>
        RasterizeToBitmap(visual, size, 1.0);

    /// <summary>
    /// 把控件光栅化到位图。
    /// </summary>
    /// <remarks>
    /// 位图的物理尺寸按缩放比放大，逻辑尺寸仍是 <paramref name="size"/>——
    /// 于是这正好复现「一个逻辑像素被摊成 scaling 个物理像素」的显示效果。
    /// </remarks>
    private static RenderTargetBitmap RasterizeToBitmap(Visual visual, Size size, double scaling)
    {
        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Round(size.Width * scaling), (int)Math.Round(size.Height * scaling)),
            new Vector(96 * scaling, 96 * scaling));

        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>
    /// 控件实际生效的缩放上限（像素/秒）。
    /// </summary>
    /// <remarks>
    /// 判据只此一处（<see cref="WaveformView.MaximumZoom"/>）。工具若自己再推一遍，
    /// 每次调整绘制方案都得记得同步改，漏一次就会拿着一档过时的「最大放大」去测，
    /// 测出来的并不是控件允许的最放大。
    /// </remarks>
    private static double MaximumZoom(PeakPyramid pyramid, Size size)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);
        return view.MaximumZoom;
    }

    /// <summary>
    /// 检验每条波带在各种显示缩放比下的<b>物理厚度</b>是否一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「用宽线条弱化逻辑像素与物理像素不对应」这个方案的验收标准。
    /// 1 逻辑像素宽的线整条都是边缘：在 125% / 150% 这类非整数缩放比下，抗锯齿会把它的
    /// 覆盖摊到相邻两个物理像素上，摊开比例随行号以几行为周期变化，于是每条线的视觉
    /// 粗细都不同；线宽到 6 像素后，只有上下各一个物理像素参与过渡，偏差应当摊薄到百分之一量级。
    /// </para>
    /// <para>
    /// 量法：沿中心线左侧两像素处取一列，自下而上扫描，把连续的非背景像素归为一段
    /// （一条带就是一段，因为带之间有背景色的空隙）。每段的「厚度」用覆盖率加权求和，
    /// 于是半透明的边缘像素按它实际占的比例计入，而不是简单地数像素个数。
    /// </para>
    /// </remarks>
    private static void MeasureBandThickness(PeakPyramid pyramid, Size size)
    {
        double[] scalings = [1.0, 1.25, 1.5, 2.0];

        Console.WriteLine("---- 波带的物理厚度（沿中心线左侧一列测量）----");
        Console.WriteLine("  「最小」一列含线长刚好擦到测量列的带，量到的是覆盖率的一小截；");
        Console.WriteLine("  判断粗细是否恒定看「中位」与「最大」的偏差。");
        Console.WriteLine("  显示缩放   段数   最小(含擦边)  中位     最大     最大/中位偏差");

        foreach (var scaling in scalings)
        {
            var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
            Layout(view, size);

            // 首次光栅化把「载入后自动适配整曲」走完，画面才定下来。
            using (RasterizeToBitmap(view, size, scaling)) { }

            using var bitmap = RasterizeToBitmap(view, size, scaling);
            var thicknesses = MeasureColumnThickness(bitmap, scaling);

            if (thicknesses.Count == 0)
            {
                Console.WriteLine($"  {scaling,7:P0}   没有测到任何波带");
                continue;
            }

            thicknesses.Sort();
            var count = thicknesses.Count;

            if (count == 0)
            {
                Console.WriteLine($"  {scaling,7:P0}   没有测到任何波带");
                continue;
            }

            // 只保留真正被完整覆盖的带：线太短时，选定的那一列只擦到线的边缘，
            // 量出来的厚度是覆盖率的一小截，不反映线条本身的粗细。
            var median = thicknesses[count / 2];
            var full = thicknesses.Where(value => value >= median * 0.9).ToList();

            var minimum = full[0];
            var maximum = full[^1];

            Console.WriteLine(
                $"  {scaling,7:P0} {count,7} {minimum,12:F2} {median,8:F2} {maximum,8:F2} " +
                $"{(maximum - median) / median,12:P1}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 检查波形是否被画到了控件边界之外。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 窗口最上、最下那两条波带必然有一部分落在控件之外——那正是「这条带只露出半截」
    /// 应有的样子，不能为了让它们不越界就干脆不画。但控件默认不裁剪，这几像素会直接
    /// 画到相邻的工具栏和状态栏上去。这个检查就是为它设的。
    /// </para>
    /// <para>
    /// 光栅化控件自身看不出这个问题：位图边界与控件边界重合，越界部分正好被位图吃掉。
    /// 所以要把控件放进一块更大的画布再整体光栅化，四周那圈留白才是可观察的。
    /// 同时报告控件矩形<b>之内</b>的波形像素数——它若是 0，说明画布压根没渲染子控件，
    /// 此时的「越界 0」是假阳性。
    /// </para>
    /// </remarks>
    private static void MeasureOverflow(PeakPyramid pyramid, Size size)
    {
        const int margin = 40;

        (string Label, double Scroll)[] cases =
        [
            ("曲首之前", -20),
            ("曲首", 0),

            // 顶端时刻落在第一条波带内部：这条带只有下半截在窗口里，
            // 另外半截若不被裁掉就会跑到控件上方的工具栏上去。
            ("曲首内", 1.0),
            ("曲中", pyramid.DurationSeconds * 0.5),
            ("曲末", pyramid.DurationSeconds),
            ("曲末之后", pyramid.DurationSeconds + 20),
        ];

        Console.WriteLine("---- 绘制是否越出控件边界（控件四周留 40 像素空白）----");

        foreach (var (label, scrollSeconds) in cases)
        {
            var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
            var host = new Canvas
            {
                Width = size.Width + margin * 2,
                Height = size.Height + margin * 2,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x20, 0x20)),
            };

            Canvas.SetLeft(view, margin);
            Canvas.SetTop(view, margin);
            host.Children.Add(view);

            var hostSize = new Size(host.Width, host.Height);
            Layout(host, hostSize);

            // 首次布局会走完「载入后自动适配」，画面定下来之后再设滚动位置。
            using (Rasterize(host, hostSize)) { }

            view.ScrollSeconds = scrollSeconds;

            using var bitmap = Rasterize(host, hostSize);
            var (inside, outside) = CountWavePixels(bitmap, margin);

            Console.WriteLine($"  {label,-12} 越界 {outside,6}   控件内 {inside,7}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 数出「波形颜色」的像素：分别统计控件矩形之内与之外。
    /// </summary>
    /// <remarks>
    /// 判据沿用厚度测量的那个：蓝分量明显高于红、且绿也高于红。画布背景是纯红，
    /// 刻度线是加在红上的灰白，琥珀色指示器红分量最高——三者都不满足，只有波形满足。
    /// </remarks>
    private static (int Inside, int Outside) CountWavePixels(RenderTargetBitmap bitmap, int margin)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var buffer = new byte[stride * height];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        int inside = 0, outside = 0;

        for (var y = 0; y < height; y++)
        {
            var rowInside = y >= margin && y < height - margin;

            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                int blue = buffer[i], green = buffer[i + 1], red = buffer[i + 2];

                if (blue - red <= 40 || green <= red) continue;

                if (rowInside && x >= margin && x < width - margin) inside++;
                else outside++;
            }
        }

        return (inside, outside);
    }

    /// <summary>
    /// 检查缩放是否<b>一律</b>以指示条为锚点。
    /// </summary>
    /// <remarks>
    /// 判据很直接：缩放前后指示条所指的时刻必须一样。以指针为锚的旧做法在这个测量下
    /// 会随指针位置偏离，而指针位置根本无法从外部设定——所以测量把锚点当成了约定，
    /// 一旦有人改回指针锚点，这组数字立刻会散开。
    /// </remarks>
    private static void MeasureZoomAnchor(PeakPyramid pyramid, Size size)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);

        // 先走完「载入后自动适配」，之后的设置才不会被它覆盖。
        using (RasterizeToBitmap(view, size, 1.0)) { }

        double[] ratios = [0.15, 0.4, 0.75];
        double[] factors = [1.25, 1.25, 1.25, 0.8, 0.8];

        Console.WriteLine("---- 缩放是否以指示条为中心 ----");
        Console.WriteLine("  指示条位置    缩放前所指    缩放后所指      偏差");

        foreach (var ratio in ratios)
        {
            view.IndicatorRatio = ratio;
            view.PixelsPerSecond = size.Height / 60.0;
            view.ScrollSeconds = 100;

            var before = view.PlayStartSeconds;

            foreach (var factor in factors) view.ZoomBy(factor);

            var after = view.PlayStartSeconds;
            var drift = Math.Abs(after - before) * 1000;

            Console.WriteLine(
                $"  {ratio,9:P0}  {before,11:F3}s {after,12:F3}s {drift,8:F2} ms");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 检查播放中按「复位」是否把播放位置一起带回曲首。
    /// </summary>
    /// <remarks>
    /// 只改视图是不够的：适配把曲首摆到指示条上，但紧接着位置刷新会按新缩放把波形
    /// 重新对齐到<b>当前播放时刻</b>——缩放确实复位了，位置却纹丝不动，
    /// 看上去就是「复位按钮无效，还在原地往下播」。判据是复位后播放头与指示条所指
    /// 必须重合，且指示条指的是曲首。
    /// </remarks>
    private static void MeasureFitWhilePlaying(PeakPyramid pyramid, Size size)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);
        using (RasterizeToBitmap(view, size, 1.0)) { }

        // 模拟「听到一半、还放大着看细节」的状态。
        view.PixelsPerSecond = size.Height / 18.0;
        view.PlayheadSeconds = pyramid.DurationSeconds * 0.5;
        view.IsPlaying = true;

        var beforeHead = view.PlayheadSeconds;
        var beforeScroll = view.ScrollSeconds;
        var beforeZoom = view.PixelsPerSecond;

        view.FitToWindow();

        Console.WriteLine("---- 播放中按「复位」 ----");
        Console.WriteLine($"  复位前：播放头 {beforeHead,9:F3}s   顶端 {beforeScroll,9:F3}s   每秒像素 {beforeZoom,8:F2}");
        Console.WriteLine($"  复位后：播放头 {view.PlayheadSeconds,9:F3}s   顶端 {view.ScrollSeconds,9:F3}s   每秒像素 {view.PixelsPerSecond,8:F2}");
        Console.WriteLine($"  指示条所指 {view.PlayStartSeconds:F3}s   "
            + $"与播放头相差 {Math.Abs(view.PlayheadSeconds - view.PlayStartSeconds) * 1000:F2} ms");
        Console.WriteLine();
    }

    /// <summary>
    /// 检查「拖动波形定位」在播放中与未播放时的两条路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 关键不在位移算得对不对（那只是一次除法），而在两者的分野。未播放时位置由
    /// <c>PlayStartSeconds</c> 逐帧送出去，松手不必额外做什么；播放时那个值会被视图模型
    /// 刻意忽略，必须等松手把播放头推一把才生效。判据因此是——<b>拖动途中播放头纹丝不动，
    /// 松手那一刻它精确落到指示条所指的时刻</b>。
    /// </para>
    /// <para>
    /// 必须这样分的理由：提交一次就走一遍 <c>AudioPlayer.Play</c>，而它会先
    /// <c>waveOutReset</c> 清掉设备缓冲；鼠标一秒能移动几十次，每次都 reset 的话
    /// 声音就碎成一串咔嗒。
    /// </para>
    /// <para>
    /// 另有一项不能省的检查：拖动途中播放器照常刷新位置，跟随逻辑必须让开，
    /// 否则刚拖动的内容会被下一拍拉回原处——那等于拖动根本没发生过。
    /// </para>
    /// </remarks>
    private static void MeasureWaveformDrag(PeakPyramid pyramid, Size size)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);
        using (RasterizeToBitmap(view, size, 1.0)) { }

        var pixelsPerSecond = size.Height / 18.0;
        view.PixelsPerSecond = pixelsPerSecond;

        Console.WriteLine("---- 拖动波形定位 ----");
        Console.WriteLine($"  每屏 18 秒时 1 像素 = {1000 / pixelsPerSecond:F1} ms");

        // ① 未播放：位置随拖动逐帧送出，松手不必额外做什么。
        view.ScrollSeconds = 60;
        var idleBefore = view.PlayStartSeconds;

        view.BeginWaveformDrag(300);
        view.UpdateWaveformDrag(310);
        var idleDuring = view.PlayStartSeconds;
        view.EndWaveformDrag();

        Console.WriteLine(
            $"  未播放   位置 {idleBefore:F3}s ──拖 10 像素──▶ {idleDuring:F3}s"
            + $"（{1000 * (idleBefore - idleDuring):F1} ms）");

        // ② 播放中：途中一次都不提交，松手才落定。
        view.ScrollSeconds = 60;
        view.PlayheadSeconds = 60;
        view.IsPlaying = true;

        var headBefore = view.PlayheadSeconds;
        var scrollBefore = view.ScrollSeconds;

        view.BeginWaveformDrag(300);
        view.UpdateWaveformDrag(280);
        var headDuring = view.PlayheadSeconds;
        var scrollDuring = view.ScrollSeconds;

        // 拖动途中播放器照常刷新位置。跟随逻辑必须让开，否则内容会被拉回去。
        view.PlayheadSeconds = 60.05;
        view.PlayheadSeconds = 60.10;
        var scrollAfterRefresh = view.ScrollSeconds;

        var target = view.PlayStartSeconds;
        view.EndWaveformDrag();
        var headAfter = view.PlayheadSeconds;

        Console.WriteLine(
            $"  播放中   顶端 {scrollBefore:F3}s ──拖 20 像素──▶ {scrollDuring:F3}s"
            + $"（内容跟手；位置刷新两次后仍是 {scrollAfterRefresh:F3}s）");
        Console.WriteLine(
            $"           播放头 {headBefore:F3}s ──▶ 拖动途中 {headDuring:F3}s（不提交）"
            + $"──▶ 松手后 {headAfter:F3}s");
        Console.WriteLine(
            $"           目标 {target:F3}s，与松手结果相差 {Math.Abs(headAfter - target) * 1000:F3} ms");

        Console.WriteLine();
    }

    /// <summary>沿一列统计各条波带的覆盖率加权厚度。</summary>
    private static List<double> MeasureColumnThickness(RenderTargetBitmap bitmap, double scaling)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var buffer = new byte[stride * height];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // 中心线自身是 1 逻辑像素宽的白线，取它左边两像素以避开。
        var x = Math.Max(0, (int)Math.Round(width / 2.0) - Math.Max(2, (int)Math.Ceiling(2 * scaling)));

        // 背景 #0E1014 的蓝分量是 20，左声道饱和处是 252。
        const double Background = 20;
        const double Saturated = 252;

        var result = new List<double>();
        double accumulated = 0;

        for (var y = 0; y < height; y++)
        {
            var i = y * stride + x * 4;
            int blue = buffer[i], green = buffer[i + 1], red = buffer[i + 2];

            var isWave = blue - red > 40 && green > red;

            if (isWave)
            {
                var coverage = (blue - Background) / (Saturated - Background);
                accumulated += Math.Clamp(coverage, 0, 1);
                continue;
            }

            // 一段结束。太薄的段多半是静音处那几个像素高的残迹，不足以反映线的厚度。
            if (accumulated >= 2) result.Add(accumulated);
            accumulated = 0;
        }

        if (accumulated >= 2) result.Add(accumulated);

        return result;
    }

    /// <summary>
    /// 解析参数。不带参数时用仓库里的 P0 素材并输出到 <c>artifacts/waveform</c>，
    /// 于是「在 Visual Studio 里直接 F5」就能出图，不必配命令行参数。
    /// </summary>
    private static (string AudioPath, string OutputDirectory) ResolveArguments(string[] args)
    {
        if (args.Length >= 2) return (args[0], args[1]);

        var repositoryRoot = FindRepositoryRoot() ?? Environment.CurrentDirectory;
        var audio = args.Length == 1 ? args[0] : FindDefaultAudio(repositoryRoot);

        return (audio, Path.Combine(repositoryRoot, "artifacts", "waveform"));
    }

    private static string FindDefaultAudio(string repositoryRoot)
    {
        string[] names = ["test.mp3", "test.flac", "test.aac", "test.ogg"];
        string[] directories = [".", "samples"];

        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(repositoryRoot, directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return Path.Combine(repositoryRoot, names[0]);
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "third_party"))) return directory.FullName;
        }

        return null;
    }

    /// <summary>渲染所需的最小应用外壳：只装一份 Fluent 主题，没有任何窗口或视图模型。</summary>
    private sealed class PreviewShell : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
