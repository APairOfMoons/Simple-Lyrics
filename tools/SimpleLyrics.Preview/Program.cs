using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using SimpleLyrics.Controls;
using SimpleLyrics.Core.Editing;
using SimpleLyrics.Core.Lyrics;
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

    /// <summary>主窗口的波形栏宽度，与 <c>MainWindow.axaml</c> 里的 WaveformColumnWidth 资源一致。</summary>
    private const int WaveformColumnWidth = 330;

    /// <summary>主窗口 1280 宽减去左右两栏之后，中栏的一半。</summary>
    private const int LyricColumnWidth = 325;

    /// <summary>本轮检查里断言了多少条、失败了多少条。</summary>
    private static int _checks;
    private static int _failed;

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

        // ⑦⑧⑨ 歌词标识层横跨「波形栏 + 已绑定歌词栏」，同一份歌词看三档缩放：
        //       整曲时密集处合成色块，放大后自行散开成一行行标签。
        var lyrics = BuildDemoLyrics();

        RenderLyricScene(outputDirectory, "7-歌词标识-整曲", pyramid, size,
            pixelsPerSecond: size.Height / duration, scrollSeconds: -0.4 * duration, lyrics: lyrics);

        RenderLyricScene(outputDirectory, "8-歌词标识-每屏40秒-含色块", pyramid, size,
            pixelsPerSecond: size.Height / 40.0, scrollSeconds: 98, lyrics: lyrics);

        RenderLyricScene(outputDirectory, "9-歌词标识-最大放大-逐行展开", pyramid, size,
            pixelsPerSecond: MaximumZoom(pyramid, size), scrollSeconds: 120, lyrics: lyrics);

        // ⑩ 框选：矩形选区框横跨歌词栏，落在其中的标识线整条高亮。
        RenderLyricScene(outputDirectory, "10-歌词选中-框选", pyramid, size,
            pixelsPerSecond: size.Height / 40.0, scrollSeconds: 100, lyrics: lyrics,
            pose: track =>
            {
                var x = WaveformColumnWidth + 60;
                track.BeginGesture(new Point(x, 20), KeyModifiers.None);
                track.ContinueGesture(new Point(x, 360));
            });

        // ⑪ 拖动中：整条标识线连同它的标签跟着指针走，而**别的什么都没动**——
        //    拖动期间刻意不重算合并与省略，所以它压住了下面的标签也照画不误，
        //    这一帧里能直接看到「松手之后才会重新排版」这件事。
        RenderLyricScene(outputDirectory, "11-歌词选中-拖动中", pyramid, size,
            pixelsPerSecond: size.Height / 40.0, scrollSeconds: 100, lyrics: lyrics,
            pose: track =>
            {
                var x = WaveformColumnWidth + 60;

                // 长句那条在 y≈184，往下拖 140 像素之后它的标签正好压住 118.5 秒那条。
                track.BeginGesture(new Point(x, 184), KeyModifiers.None);
                track.ContinueGesture(new Point(x, 324));
            });

        // ⑫ 松手之后：新位置与 118.5 秒那条并成了一块，但仍然看得出哪一条是刚拖过来的——
        //    选中的那一条在色块里被铺成一条灰带，块上的字也报出「已选 1」。
        //    这一帧在上一轮是不能看的：当时的画法按选区把色块拆散了，看着像「合并算错了」。
        //
        // 给这一幕单发一份<b>拷贝</b>：拖动落定会就地改行对象，而这份示范歌词还要给后面的
        // 测量用——共用一份的话，量到的是被这一幕改过的数据（踩过一次，表现为后面整段跳过）。
        RenderLyricScene(outputDirectory, "12-合并之后-选中部分仍可辨认", pyramid, size,
            pixelsPerSecond: size.Height / 40.0, scrollSeconds: 100, lyrics: Clone(lyrics),
            pose: track => DropSelection(track, 184, 324));

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

        // 滚轮：波形栏与已绑定歌词栏共用同一条路径。
        MeasureWheel(pyramid, size);

        // 歌词标识线的合并规律，以及「分组不丢行、不重行」。
        MeasureLyricMarks(pyramid, size, lyrics);

        // 两条标识线不合并的最小间隔，以及 LRC 时间码带来的取整。
        MeasureMinimumSeparation(pyramid, size);

        // 歌词栏里的选中与拖动：命中、位移换算、以及「拖动途中不动文档」这条前提。
        MeasureLyricEditing(pyramid, size, lyrics);

        // 拖动落定后与邻居合并时的画法：形状只由时间码决定，选区只换颜色。
        MeasureMergedSelection(size);

        return 0;
    }

    /// <summary>
    /// 检查滚轮的那条路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 滚轮的处理本来是 <c>OnPointerWheelChanged</c> 里的一段，为了让「已绑定歌词栏」也能用，
    /// 被抽成了 <see cref="WaveformView.ApplyWheel"/> 并交由那一栏转发。抽取本身有风险——
    /// 抽错了不会有任何编译错误——所以这里直接把三条语义钉住：向上滚看到更早的时间、
    /// Ctrl+滚轮仍然以指示条为锚、没有音频时如实返回「没处理」。
    /// </para>
    /// <para>
    /// 最后一条不是凑数：那一栏拿到 <c>false</c> 才会把事件继续冒泡出去。
    /// 若它一律返回 <c>true</c>，一个空窗口里的滚轮就会被无声吞掉。
    /// </para>
    /// </remarks>
    private static void MeasureWheel(PeakPyramid pyramid, Size size)
    {
        var view = new WaveformView { Width = size.Width, Height = size.Height, Peaks = pyramid };
        Layout(view, size);
        using (RasterizeToBitmap(view, size, 1.0)) { }

        view.PixelsPerSecond = size.Height / 60.0;
        view.IndicatorRatio = 0.4;
        view.ScrollSeconds = 100;

        Console.WriteLine("---- 滚轮 ----");

        var before = view.ScrollSeconds;
        var handledUp = view.ApplyWheel(1, zoom: false);

        Console.WriteLine(
            $"  向上滚一格：顶端 {before,8:F2}s → {view.ScrollSeconds,8:F2}s"
            + $"（看到更早的时间：{(view.ScrollSeconds < before ? "是" : "否")}，已处理 {handledUp}）");

        before = view.ScrollSeconds;
        var handledDown = view.ApplyWheel(-1, zoom: false);

        Console.WriteLine(
            $"  向下滚一格：顶端 {before,8:F2}s → {view.ScrollSeconds,8:F2}s"
            + $"（看到更晚的时间：{(view.ScrollSeconds > before ? "是" : "否")}，已处理 {handledDown}）");

        var anchor = view.PlayStartSeconds;
        var zoomBefore = view.PixelsPerSecond;
        view.ApplyWheel(1, zoom: true);
        var drift = Math.Abs(view.PlayStartSeconds - anchor) * 1000;

        Console.WriteLine(
            $"  Ctrl+滚轮  ：每秒像素 {zoomBefore,7:F2} → {view.PixelsPerSecond,7:F2}"
            + $"   指示条所指 {anchor,7:F3}s → {view.PlayStartSeconds,7:F3}s   偏差 {drift:F2} ms");

        var empty = new WaveformView();
        Console.WriteLine($"  没有音频时：{(empty.ApplyWheel(1, zoom: false) ? "错误地报告已处理" : "如实报告未处理")}");

        // 拖动标识线期间缩放被锁住。锁的必须是缩放而不是滚动：手势还在进行中，
        // 让 Ctrl+滚轮降级成普通滚动，比变成一片什么都不做的死区好。
        _checks = 0;
        _failed = 0;

        view.ZoomLocked = true;

        var lockedZoom = view.PixelsPerSecond;
        var lockedScroll = view.ScrollSeconds;
        var handled = view.ApplyWheel(1, zoom: true);

        Console.WriteLine(
            $"  缩放锁住时：Ctrl+滚轮 每秒像素 {lockedZoom,7:F2} → {view.PixelsPerSecond,7:F2}"
            + $"（须不变）  顶端 {lockedScroll,8:F2}s → {view.ScrollSeconds,8:F2}s（降级成滚动）  已处理 {handled}");

        Check("锁住缩放时 Ctrl+滚轮不改变缩放", view.PixelsPerSecond == lockedZoom);
        Check("锁住缩放时 Ctrl+滚轮仍然滚动", view.ScrollSeconds != lockedScroll);
        Check("锁住缩放时这一格仍算已处理", handled);

        view.ZoomLocked = false;
        Report();

        Console.WriteLine();
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

    /// <summary>
    /// 渲染「波形栏 + 已绑定歌词栏」，歌词标识层横跨两栏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意把两个控件放进真的 <see cref="Grid"/> 里，而不是分别渲染再拼图：
    /// 这条跨栏的线是否与波形的时间轴对齐，正是要靠这张图回答的问题，
    /// 拼图会把唯一值得看的东西（接缝处的连续性）藏起来。
    /// </para>
    /// <para>
    /// <paramref name="pose"/> 用来把控件摆成某个「进行中」的状态（正在框选、正在拖动）。
    /// 那些状态在真实操作里只存在几百毫秒，却是本轮功能的主体，所以直接把它们画出来。
    /// </para>
    /// </remarks>
    private static void RenderLyricScene(
        string outputDirectory,
        string label,
        PeakPyramid pyramid,
        Size size,
        double pixelsPerSecond,
        double scrollSeconds,
        IReadOnlyList<LyricLine> lyrics,
        Action<LyricTrackView>? pose = null)
    {
        var sceneSize = new Size(WaveformColumnWidth + LyricColumnWidth, size.Height);
        var grid = BuildLyricScene(pyramid, sceneSize, lyrics, out var waveform, out var track);

        Layout(grid, sceneSize);

        // 首次光栅化把波形控件「载入后自动适配整曲」那一步走完，
        // 之后设置的缩放与滚动才不会被它覆盖。
        using (Rasterize(grid, sceneSize)) { }

        waveform.PixelsPerSecond = pixelsPerSecond;
        waveform.ScrollSeconds = scrollSeconds;

        // 标识层与波形共用同一份时间轴参数——这正是产品里那条数据绑定的路径。
        track.PixelsPerSecond = waveform.PixelsPerSecond;
        track.ScrollSeconds = waveform.ScrollSeconds;

        pose?.Invoke(track);

        waveform.InvalidateVisual();
        track.InvalidateVisual();

        var name = $"{label}.png";

        using (var bitmap = RasterizeToBitmap(grid, sceneSize, 1.0)) bitmap.Save(Path.Combine(outputDirectory, name));

        var clusters = new List<LyricMarkCluster>();
        LyricTrackView.GroupInto(clusters, lyrics, track.ScrollSeconds, track.PixelsPerSecond, LyricTrackView.LabelMinGap);

        int singles = 0, blocks = 0;
        foreach (var cluster in clusters)
        {
            if (cluster.BottomY < 0 || cluster.TopY > sceneSize.Height) continue;
            if (cluster.Count == 1) singles++;
            else blocks++;
        }

        var extra = pose is null ? string.Empty : $"   选中 {track.SelectionCount} 行";

        Console.WriteLine(
            $"已输出 {name,-34} 每秒像素 {track.PixelsPerSecond,7:F1}   "
            + $"顶端 {track.ScrollSeconds,8:F2}s   可见：单行 {singles} / 色块 {blocks}{extra}");
    }

    private static Grid BuildLyricScene(
        PeakPyramid pyramid,
        Size size,
        IReadOnlyList<LyricLine> lyrics,
        out WaveformView waveform,
        out LyricTrackView track)
    {
        waveform = new WaveformView { Peaks = pyramid };

        track = new LyricTrackView
        {
            Lyrics = lyrics,
            TextInset = WaveformColumnWidth,
        };

        var grid = new Grid
        {
            Width = size.Width,
            Height = size.Height,
            ColumnDefinitions = new ColumnDefinitions($"{WaveformColumnWidth},*"),
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11)),
        };

        var waveHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14)),
            Child = waveform,
        };

        Grid.SetColumn(waveHost, 0);
        grid.Children.Add(waveHost);

        var lyricHost = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11)) };
        Grid.SetColumn(lyricHost, 1);
        grid.Children.Add(lyricHost);

        // 标识层横跨两列，而且必须是最后一个子元素——Grid 按添加顺序绘制，最后添加的在最上面。
        Grid.SetColumn(track, 0);
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);

        return grid;
    }

    /// <summary>
    /// 造一份示范歌词：正文每 8.5 秒一句，另插两段刻意拥挤的副歌。
    /// </summary>
    /// <remarks>
    /// 拥挤段落是为了在同一张图里同时看到两种形态——单行带标签、密集处合成色块；
    /// 把同一份歌词换一档缩放再渲染，就能看到色块随放大自行散开成一行行标签。
    /// </remarks>
    private static IReadOnlyList<LyricLine> BuildDemoLyrics()
    {
        string[] verses =
        [
            "夜色漫过窗台",
            "风把云吹成一片海",
            "我数着路灯一盏盏",
            "数到天边泛白",
            "旧唱片转了半圈",
            "唱针停在某个夏天",
            "那时的蝉声很慢",
            "慢得够写完一封信",
            "后来城市改了名字",
            "街角那家店也搬走",
            "只有歌还在原地",
            "等着谁把它拾起",
            "如果时间是一条河我愿在渡口多停一会看看倒影里的自己还是当年那个模样",
            "我愿在渡口多停一会",
            "看倒影里的自己",
            "还是当年那个模样",
            "副歌响起的时候",
            "所有的灯都亮了",
            "所有的门都开了",
            "所有的路都通向",
            "那个回不去的傍晚",
            "掌声落下去又起",
            "像潮水不肯退去",
        ];

        var lines = new List<LyricLine>();

        for (var index = 0; index < verses.Length; index++)
        {
            lines.Add(new LyricLine
            {
                Text = verses[index],
                Start = TimeSpan.FromSeconds(8 + index * 8.5),
            });
        }

        // 密集段一：12 句挤在 1 秒之内。每屏 40 秒时相邻只差 3.7 像素，必然合并；
        // 放到最大放大时相邻拉开一百多像素，就该逐行展开。
        for (var index = 0; index < 12; index++)
        {
            lines.Add(new LyricLine
            {
                Text = $"和声 {index + 1}",
                Start = TimeSpan.FromSeconds(120 + index * 0.09),
            });
        }

        // 密集段二：间距稍大，用来看「刚好放得下标签」与「放不下」的交界。
        for (var index = 0; index < 5; index++)
        {
            lines.Add(new LyricLine
            {
                Text = $"叠句 {index + 1}",
                Start = TimeSpan.FromSeconds(180 + index * 0.35),
            });
        }

        // 标识层要求按时间升序，这里显式排一次——产品里由 LyricDocument.TimedLines 保证。
        lines.Sort(static (left, right) => Nullable.Compare(left.Start, right.Start));
        return lines;
    }

    /// <summary>
    /// 检查歌词标识线的合并规律与标签排版。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据有四条。其一是<b>分组完整性</b>：每一行都必须落在某一个簇里，既不丢也不重——
    /// 丢一行是「歌词没画出来」，重一行是「凭空多一条线」，两者都不会崩，只会悄悄错。
    /// </para>
    /// <para>
    /// 其二是<b>合并随缩放散开</b>：同一份歌词放大之后，被卷进色块的行数必须下降。
    /// 这正是「判据取像素而不是秒」要保证的事；若有人改回按时间判，这一列立刻就不动了。
    /// </para>
    /// <para>
    /// 其三是<b>完整渲染的条数随缩放上升</b>：放大之后间距变宽、可排的行数变多，
    /// 被省略号的条数应当降到零。这是本轮改动的核心，所以直接数出来。
    /// </para>
    /// <para>
    /// 其四是<b>门槛仍然够用</b>：<see cref="LyricTrackView.LabelMinGap"/> 是手写常数，
    /// 而一行标签实际需要的高度随字号变化。门槛一旦小于实际所需，两条单行标签就会互相压住，
    /// 而那是一种「不崩、只是慢慢变难读」的缺陷，肉眼很难发现。
    /// </para>
    /// <para>
    /// 分组与排版都用控件自己的 <see cref="LyricTrackView.GroupInto"/> 与
    /// <see cref="LyricTrackView.PlanLabel"/>，不是这里另写一遍——
    /// 另写一遍的话，测的就不是屏幕上真正发生的事了。
    /// </para>
    /// </remarks>
    private static void MeasureLyricMarks(PeakPyramid pyramid, Size size, IReadOnlyList<LyricLine> lyrics)
    {
        var duration = pyramid.DurationSeconds;
        if (duration <= 0 || lyrics.Count == 0) return;

        var sceneWidth = WaveformColumnWidth + LyricColumnWidth;
        var available = LyricTrackView.TextAreaWidth(sceneWidth, WaveformColumnWidth);
        var required = LyricTrackView.RequiredRowGap();

        Console.WriteLine("---- 歌词标签的排版 ----");
        Console.WriteLine("  时间戳打头、正文接在后面，排不下就往下一行折；行数由「到下一条标识线的距离」决定。");
        Console.WriteLine(
            $"  单行标签需要 {required:F1} 像素，合并门槛 {LyricTrackView.LabelMinGap:F0} 像素 —— "
            + $"{(LyricTrackView.LabelMinGap >= required ? "通过" : "失败：两条单行标签会互相压住")}");
        Console.WriteLine($"  正文字段宽 {available:F1} 像素");

        (string Label, double PixelsPerSecond, double Scroll)[] cases =
        [
            ("整曲适配（每屏一曲）", size.Height / duration, -0.4 * duration),
            ("每屏 160 秒", size.Height / 160.0, 55),
            ("每屏 60 秒", size.Height / 60.0, 60),
            ("每屏 40 秒", size.Height / 40.0, 98),
            ("每屏 18 秒", size.Height / 18.0, 112),
            ("最大放大（已钳制）", MaximumZoom(pyramid, size), 120),
        ];

        Console.WriteLine();
        Console.WriteLine("  场景                    可见行   色块   单条   完整   省略   行数范围");

        var clusters = new List<LyricMarkCluster>();
        var grouped = 0;

        foreach (var (label, pixelsPerSecond, scroll) in cases)
        {
            LyricTrackView.GroupInto(clusters, lyrics, scroll, pixelsPerSecond, LyricTrackView.LabelMinGap);

            int visible = 0, blocks = 0, singles = 0, complete = 0, truncated = 0;
            var minimumRows = int.MaxValue;
            var maximumRows = 0;

            for (var index = 0; index < clusters.Count; index++)
            {
                var cluster = clusters[index];

                // 完整性要连屏幕外的簇一起数：分组是全局的，不能只在可见部分成立。
                grouped += cluster.Count;

                if (cluster.BottomY < 0 || cluster.TopY > size.Height) continue;

                visible += cluster.Count;

                if (cluster.Count > 1)
                {
                    blocks++;
                    continue;
                }

                singles++;

                // 行数与「完整 / 省略」都由控件自己的判据给出。
                var room = index + 1 < clusters.Count
                    ? clusters[index + 1].TopY - cluster.TopY - 3
                    : double.PositiveInfinity;

                var plan = LyricTrackView.PlanLabel(lyrics[cluster.Start], available, room);

                if (plan.Truncated) truncated++;
                else complete++;

                if (plan.Rows < minimumRows) minimumRows = plan.Rows;
                if (plan.Rows > maximumRows) maximumRows = plan.Rows;
            }

            var range = singles == 0 ? "—" : $"{minimumRows} – {maximumRows} 行";

            Console.WriteLine(
                $"  {label,-24} {visible,6} {blocks,6} {singles,6} {complete,6} {truncated,6}   {range}");
        }

        var expected = lyrics.Count * cases.Length;

        Console.WriteLine();
        Console.WriteLine(
            $"  分组完整性：{lyrics.Count} 行 × {cases.Length} 档，应计入 {expected}，实际计入 {grouped}"
            + $" —— {(grouped == expected ? "通过（无丢失、无重复）" : "失败")}");

        // 分组与行数与滚动位置无关。这一条是**拖动期间不重算合并与省略**的地基：
        // 时间轴数据在松手前没被改动，于是这一帧算出来的簇与行数和按下之前逐字相同。
        // 若哪天有人把判据改成看绝对纵坐标（「快到底了就少画两行」），这一列立刻就会动。
        var shape = LyricShape(lyrics, size, size.Height / 60.0, 60);
        var drift = 0;

        for (var offset = 1; offset <= 8; offset++)
        {
            if (LyricShape(lyrics, size, size.Height / 60.0, 60 + offset * 3.7) == shape) continue;
            drift++;
        }

        Console.WriteLine(
            $"  分簇与行数随滚动的不变：同一档缩放下挪 8 次滚动位置，形状变了 {drift} 次"
            + $" —— {(drift == 0 ? "通过" : "失败：拖动时会重算版式")}");
        Console.WriteLine($"  （形状指纹 {shape[..Math.Min(48, shape.Length)]}…）");

        Console.WriteLine();
    }

    /// <summary>
    /// 「某一档缩放、某个滚动位置下，屏幕上的分簇与行数」的指纹。
    /// </summary>
    /// <remarks>
    /// 只取「哪几行在同一簇里 + 每簇的标签占几行」，不含纵坐标——要测的正是它与滚动无关。
    /// </remarks>
    private static string LyricShape(IReadOnlyList<LyricLine> lyrics, Size size, double pixelsPerSecond, double scroll)
    {
        var available = LyricTrackView.TextAreaWidth(WaveformColumnWidth + LyricColumnWidth, WaveformColumnWidth);

        var clusters = new List<LyricMarkCluster>();
        LyricTrackView.GroupInto(clusters, lyrics, scroll, pixelsPerSecond, LyricTrackView.LabelMinGap);

        var builder = new System.Text.StringBuilder();

        for (var index = 0; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            var room = index + 1 < clusters.Count
                ? clusters[index + 1].TopY - cluster.TopY - 3
                : double.PositiveInfinity;

            var rows = cluster.Count == 1
                ? LyricTrackView.PlanLabel(lyrics[cluster.Start], available, room).Rows
                : 0;

            builder.Append(cluster.Start).Append('/').Append(cluster.Count).Append(':').Append(rows).Append(' ');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 检查「两条标识线在最大放大倍率下不合并」这条最小间隔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据不另写一套，而是拿两条按该间隔摆好的歌词去问控件自己的分簇算法：正好间隔时必须是
    /// 两条独立的线，少一格就必须粘成一个色块。后半句同样重要——若少一格也分得开，
    /// 说明这个间隔定得偏大，白白浪费了时间轴的精度。
    /// </para>
    /// <para>
    /// 这里同时钉住「LRC 只存得到 10 毫秒」这件事：理论上限 15.96 毫秒不是可写出的时间码，
    /// 所以实际执行的是向上取整后的整格数（20 毫秒）。
    /// </para>
    /// </remarks>
    private static void MeasureMinimumSeparation(PeakPyramid pyramid, Size size)
    {
        _checks = 0;
        _failed = 0;

        var maximum = MaximumZoom(pyramid, size);
        var minimum = LyricTrackView.MinimumSeparationFor(maximum);
        var units = LyricTimeline.MinimumUnits(minimum);
        var enforced = LrcTimecode.FromUnits(units);

        Console.WriteLine("---- 两条标识线不合并的最小间隔 ----");
        Console.WriteLine(
            $"  最大放大倍率 {maximum,8:F2} 像素/秒   合并门槛 {LyricTrackView.LabelMinGap:F0} 像素");
        Console.WriteLine(
            $"  理论最小间隔 {minimum.TotalMilliseconds,8:F3} 毫秒   "
            + $"按 {LrcTimecode.Resolution.TotalMilliseconds:F0} 毫秒时间码向上取整 → "
            + $"实际执行 {enforced.TotalMilliseconds:F0} 毫秒（{units} 格）");
        Console.WriteLine();

        double[] cases = [enforced.TotalSeconds, (units - 1) * 0.01];
        var expectation = new[] { "两条独立标识线", "合成一个色块" };

        for (var index = 0; index < cases.Length; index++)
        {
            var seconds = cases[index];

            var lines = new List<LyricLine>
            {
                new() { Text = "前一句", Start = TimeSpan.FromSeconds(60) },
                new() { Text = "后一句", Start = TimeSpan.FromSeconds(60 + seconds) },
            };

            var clusters = new List<LyricMarkCluster>();
            LyricTrackView.GroupInto(clusters, lines, 0, maximum, LyricTrackView.LabelMinGap);

            var gap = seconds * maximum;
            var outcome = clusters.Count == 1 ? "合成一个色块" : "两条独立标识线";

            Console.WriteLine(
                $"  间隔 {seconds * 1000,5:F0} 毫秒 → 屏幕间距 {gap,7:F2} 像素 → {outcome}"
                + $"（期望 {expectation[index]}）");

            Check($"间隔 {seconds * 1000:F0} 毫秒时的合并行为", outcome == expectation[index]);
        }

        Report();
    }

    /// <summary>
    /// 检查歌词栏里的选中与拖动。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里不用「自己算一遍再比对」的办法，而是真的对控件做一遍操作：
    /// <see cref="LyricTrackView.BeginGesture"/> / <c>ContinueGesture</c> / <c>EndGesture</c>
    /// 就是窗口代码后置接到指针事件后调用的那三个方法。于是这一组检查同时守住了三件事——
    /// 命中判据、拖动位移的换算、以及「拖动期间不改动文档」这条让合并与省略得以不重算的前提。
    /// </para>
    /// <para>
    /// 每个簇的顶端纵坐标来自控件自己的分簇算法，工具不另推一遍。
    /// </para>
    /// </remarks>
    private static void MeasureLyricEditing(PeakPyramid pyramid, Size size, IReadOnlyList<LyricLine> lyrics)
    {
        _checks = 0;
        _failed = 0;

        const double Scroll = 100;
        const double DragPixels = 120;

        var sceneWidth = WaveformColumnWidth + LyricColumnWidth;
        var scene = new Size(sceneWidth, size.Height);
        var pixelsPerSecond = size.Height / 40.0;

        var track = new LyricTrackView
        {
            Width = sceneWidth,
            Height = size.Height,
            TextInset = WaveformColumnWidth,
            Lyrics = lyrics,
            MinimumSeparation = LyricTrackView.MinimumSeparationFor(MaximumZoom(pyramid, size)),
        };

        Layout(track, scene);
        track.PixelsPerSecond = pixelsPerSecond;
        track.ScrollSeconds = Scroll;

        var clusters = new List<LyricMarkCluster>();
        LyricTrackView.GroupInto(clusters, lyrics, Scroll, pixelsPerSecond, LyricTrackView.LabelMinGap);

        Console.WriteLine("---- 歌词的选中与拖动 ----");

        if (clusters.Count < 3)
        {
            Console.WriteLine("  示范歌词太少，跳过。");
            Console.WriteLine();
            return;
        }

        // 挑一条单独的、正文长到要折行的标识线：拖它最能看出「标签跟着走、版式却没重算」。
        var target = -1;
        for (var index = 0; index < clusters.Count; index++)
        {
            if (clusters[index].Count != 1) continue;
            if (lyrics[clusters[index].Start].Text.Length < 20) continue;

            target = index;
            break;
        }

        if (target < 0 || target + 2 >= clusters.Count)
        {
            Console.WriteLine("  示范歌词里没有合适的单行目标，跳过。");
            Console.WriteLine();
            return;
        }

        var x = WaveformColumnWidth + 60;
        var line = lyrics[clusters[target].Start];
        var original = line.Start!.Value;
        var topY = clusters[target].TopY;

        IReadOnlyList<LyricTimeTarget>? reported = null;
        track.TimesChanged += (_, e) => reported = e.Targets;

        // ① 拖一段距离：报出来的时刻必须是「原时刻 + 位移对应的秒数」，并且量化到时间码。
        track.BeginGesture(new Point(x, topY), KeyModifiers.None);
        track.ContinueGesture(new Point(x, topY + DragPixels));

        var duringDrag = line.Start!.Value;

        track.EndGesture();

        var wanted = LrcTimecode.Quantize(original + TimeSpan.FromSeconds(DragPixels / pixelsPerSecond));
        var landed = reported is { Count: > 0 } ? reported[0].Start : original;

        Console.WriteLine(
            $"  拖动 {DragPixels:F0} 像素 = {DragPixels / pixelsPerSecond * 1000:F0} 毫秒："
            + $"{LrcTimecode.Format(original)} → {LrcTimecode.Format(landed)}"
            + $"（期望 {LrcTimecode.Format(wanted)}）");
        Console.WriteLine(
            $"  拖动途中文档里的时间码 {LrcTimecode.Format(duringDrag)}"
            + $"（{(duringDrag == original ? "未改动" : "被改了")}）");

        Check("拖动途中不动文档", duringDrag == original);
        Check("落点 = 原时刻位移后量化到时间码", reported is { Count: 1 } && reported[0].Start == wanted);
        Check("松手只报警不改文档", line.Start == original);
        Check("拖过的行成为选中项", track.SelectionCount == clusters[target].Count);

        // ② 只点一下：一次点击不该动任何时间码——拖动会真的改数据，不能因为手抖几像素就动它。
        reported = null;
        track.BeginGesture(new Point(x, topY), KeyModifiers.None);
        track.EndGesture();

        Check("只点一下不改时间码", reported is null);
        Check("点已选中的行不改变选区", track.SelectionCount == clusters[target].Count);

        // ③ Ctrl 加选下一簇、Shift 从锚点选到再下一簇。
        var second = clusters[target + 1];
        var third = clusters[target + 2];

        track.BeginGesture(new Point(x, second.TopY), KeyModifiers.Control);
        track.EndGesture();

        var afterControl = track.SelectionCount;
        var expectedControl = clusters[target].Count + second.Count;

        Console.WriteLine(
            $"  Ctrl 点击相邻色块：选中 {afterControl} 行（期望 {expectedControl} 行 = "
            + $"{clusters[target].Count} + {second.Count}）");

        Check("Ctrl 点击把整簇并进选区", afterControl == expectedControl);

        track.BeginGesture(new Point(x, third.TopY), KeyModifiers.Shift);
        track.EndGesture();

        var afterShift = track.SelectionCount;
        var expectedShift = third.End - second.Start;

        Console.WriteLine($"  Shift 点击再下一簇：选中 {afterShift} 行（期望 {expectedShift} 行）");

        Check("Shift 点击选中整段区间", afterShift == expectedShift);

        // ④ 框选：从空白处往下拉一个矩形，落在其中的每一簇都该被选中。
        var first = clusters[target];
        var last = clusters[target + 2];

        var from = first.TopY - 10;
        var to = last.TopY + 10;

        track.BeginGesture(new Point(x, from), KeyModifiers.None);
        track.ContinueGesture(new Point(x, to));
        track.EndGesture();

        var covered = first.Count + second.Count + third.Count;

        // 上一簇的标签有可能一直伸到矩形上沿之下——那它就一并被选中，这是有意的
        // （标签是上一条标识线的一部分，点/框到文字就算选到它）。两种结果都算对。
        var previous = target > 0 ? clusters[target - 1].Count : 0;

        Console.WriteLine(
            $"  框选 [{from:F0}, {to:F0}] 像素：选中 {track.SelectionCount} 行"
            + $"（区间内三簇共 {covered} 行"
            + (previous > 0 ? $"，上一簇的标签若伸进来还会加上 {previous} 行" : string.Empty)
            + "）");

        Check(
            "框选选中区间内的标识线",
            track.SelectionCount == covered || track.SelectionCount == covered + previous);

        // ⑤ Esc 放弃：拖动过程中文档从未改动，所以「放弃」就是把预览丢掉，一个字都不用还原。
        reported = null;
        var before = line.Start;

        track.BeginGesture(new Point(x, topY), KeyModifiers.None);
        track.ContinueGesture(new Point(x, topY + DragPixels * 2));
        track.AbortGesture();

        Check("放弃拖动不报出改动", reported is null);
        Check("放弃拖动后时间码原样", line.Start == before);

        // ⑥ 播放中滚动不改变「谁被拖到哪儿」：指针下的时刻跟着滚动走，被拖的线因此牢牢贴在指针下面。
        track.BeginGesture(new Point(x, topY), KeyModifiers.None);
        track.ContinueGesture(new Point(x, topY + 40));

        track.ScrollSeconds += 5;                       // 播放中波形自己往前滚
        track.ContinueGesture(new Point(x, topY + 40)); // 指针一动不动
        track.EndGesture();

        var afterScroll = reported is { Count: > 0 } ? reported[0].Start : original;
        var withScroll = original.TotalSeconds + 40 / pixelsPerSecond + 5;

        Console.WriteLine(
            $"  拖动途中波形滚动 5 秒：落点 {LrcTimecode.Format(afterScroll)}"
            + $"（指针没动，线条因此贴在指针下，落点随之 +5 秒）");

        Check("波形滚动把落点一起带走", Math.Abs(afterScroll.TotalSeconds - withScroll) < 0.011);

        // ⑦ 选中样式必须真的落到画面上。
        // 这一条只能靠数像素：选中色画丢了不会崩、不会报错，而「拉了框却看不出选了谁」
        // 正是本轮从渲染图里发现的一个真实缺陷（当时「选中」与「正在拖动」被混成了一个判断）。
        //
        // 先把视图滚回 ⑥ 之前的位置：⑥ 末尾滚动过 5 秒，那个纵坐标这时已经指到别处了。
        track.ScrollSeconds = Scroll;
        track.ClearSelection();

        var empty = CountWhite(RasterizeToBitmap(track, scene, 1.0));

        track.BeginGesture(new Point(x, topY), KeyModifiers.None);
        track.EndGesture();

        var frame = RasterizeToBitmap(track, scene, 1.0);
        var one = CountWhite(frame);

        // 选中色一度与播放指示条同为琥珀，实机根本分不清。这条断言把「两者不同色」
        // 钉死：标识层里任何情况下都不该出现那个琥珀（它只属于播放指示头）。
        var amber = CountAmber(frame);

        Console.WriteLine(
            $"  白色（选中）像素：未选中 {empty} 个，选中一条 {one} 个"
            + $"（控制宽 {sceneWidth} 像素，4 像素粗的线约 {sceneWidth * 3} 个）；"
            + $"琥珀（播放指示条专用）{amber} 个");

        Check("按下的那一下确实选中了一行", track.SelectionCount == clusters[target].Count);
        Check("没有选中项时画面上没有选中色", empty == 0);
        Check("选中之后标识线被画成选中样式", one > sceneWidth * 2);
        Check("选中色与播放指示条不同色", amber == 0);

        Report();
    }

    /// <summary>
    /// 数出「选中色」的像素。
    /// </summary>
    /// <remarks>
    /// 判据取<b>完全等于</b>白色，而不是「够亮就算」：文字的抗锯齿会在笔画边缘留下
    /// 彩边（次像素平滑），宽松的判据会把它们一起数进来，于是「没选中也有几百个」——
    /// 这种假阳性会让这条检查彻底失去意义。正文用近白（253,242,248）而不是纯白，
    /// 正是为了让「纯白」只属于选中。
    /// </remarks>
    private static int CountWhite(RenderTargetBitmap bitmap) =>
        CountPixels(bitmap, static (red, green, blue) => red == 255 && green == 255 && blue == 255);

    /// <summary>数出播放指示条专用的琥珀色（255,194,74）。</summary>
    private static int CountAmber(RenderTargetBitmap bitmap) =>
        CountPixels(bitmap, static (red, green, blue) => red == 255 && green == 194 && blue == 74);

    /// <summary>数出满足条件的像素个数。</summary>
    private static int CountPixels(RenderTargetBitmap bitmap, Func<int, int, int, bool> match)
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

        var count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * stride + x * 4;
                if (match(buffer[index + 2], buffer[index + 1], buffer[index])) count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 「横贯整个宽度的那些行」的指纹：标识线与色块的所在行，形如 <c>302+7;352+3</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据刻意与颜色无关：逐行数不透明像素，占满大部分宽度的那些行就是标识线与色块
    /// （正文与时间戳都只有几十像素宽，铺不满）。于是这份指纹只描述<b>形状</b>——
    /// 哪几条横线存在、各占几行、在什么位置。
    /// </para>
    /// <para>
    /// 用它来守住本轮那个真缺陷：一个色块拖到另一个色块附近松手之后，新合并出来的簇里
    /// 既有选中的又有没选中的，而当时的画法按选区把色块拆开了，看上去像是「合并算错了，
    /// 非得取消选择才重新合并」。现在把「有选区」与「没选区」两份指纹一比：
    /// 选区只该换颜色，形状一旦有任何差别，这份指纹立刻就不同了。
    /// </para>
    /// </remarks>
    private static string BandSignature(RenderTargetBitmap bitmap)
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

        var runs = new List<string>();
        var run = 0;

        for (var y = 0; y < height; y++)
        {
            var ink = 0;

            for (var x = 0; x < width; x++)
            {
                if (buffer[y * stride + x * 4 + 3] >= 16) ink++;
            }

            if (ink * 4 >= width * 3)
            {
                run++;
                continue;
            }

            if (run == 0) continue;

            runs.Add($"{y - run}+{run}");
            run = 0;
        }

        if (run > 0) runs.Add($"{height - run}+{run}");

        return string.Join(";", runs);
    }

    /// <summary>
    /// 把指纹拆成一段段。
    /// </summary>
    /// <remarks>
    /// 两份指纹是<b>逐字</b>比较的，不再留任何容差。曾经留过 1 像素（选中的标识线粗一倍，
    /// 描线画在色块边缘时会多探出 1 像素），而那个 1 像素本身就不该存在——现在块内选中的
    /// 那几条改成了<b>填充</b>（灰带，夹在色块之内），块的轮廓因此只由时间码决定，
    /// 有选区与没选区画出来逐字节相同。判据收紧之后，任何形状变化都跑不掉。
    /// </remarks>
    private static List<(int Start, int End)> ParseRuns(string signature)
    {
        var runs = new List<(int Start, int End)>();
        if (signature.Length == 0) return runs;

        foreach (var part in signature.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('+');
            var start = int.Parse(pieces[0]);
            runs.Add((start, start + int.Parse(pieces[1]) - 1));
        }

        return runs;
    }

    /// <summary>
    /// 检查「拖动落定之后与邻居合并」时的画法。
    /// </summary>
    /// <remarks>
    /// 实机反馈的那一步：把一个色块拖到另一个色块<b>附近或重叠</b>再松手。
    /// 判据见 <see cref="BandSignature"/>：形状只由时间码决定，选区只换颜色。
    /// </remarks>
    private static void MeasureMergedSelection(Size size)
    {
        _checks = 0;
        _failed = 0;

        const double Near = 18.4;   // 相距 1 秒的相邻两句只有 18.4 像素 < 合并门槛 22 → 合成一块
        const double Far = 60.0;    // 同一对相距 60 像素 > 门槛 → 各自独立

        var scene = new Size(WaveformColumnWidth + LyricColumnWidth, size.Height);

        Console.WriteLine("---- 与邻居合并之后的画法 ----");

        // ① 选中一条，然后缩小到它和邻居合并成一簇：这一簇里既有选中的又有没选中的。
        var a = new LyricLine { Text = "先被选中的那一句", Start = TimeSpan.FromSeconds(10) };
        var b = new LyricLine { Text = "后合并进来的那一句", Start = TimeSpan.FromSeconds(11) };
        var pair = new List<LyricLine> { a, b };

        var chosen = NewTrack(scene, pair);
        chosen.PixelsPerSecond = Far;
        chosen.ScrollSeconds = 9.5;
        chosen.BeginGesture(new Point(WaveformColumnWidth + 60, (10 - 9.5) * Far), KeyModifiers.None);
        chosen.EndGesture();
        chosen.PixelsPerSecond = Near;

        var plain = NewTrack(scene, pair);
        plain.PixelsPerSecond = Near;
        plain.ScrollSeconds = 9.5;

        var withSelection = BandSignature(RasterizeToBitmap(chosen, scene, 1.0));
        var without = BandSignature(RasterizeToBitmap(plain, scene, 1.0));

        Console.WriteLine(
            $"  选中一条之后缩小到与邻句合并（{LyricTrackView.LabelMinGap:F0} 像素门槛，间距 {Near:F1} 像素）");
        Console.WriteLine($"    有选区（选中 {chosen.SelectionCount} 行） {withSelection}");
        Console.WriteLine($"    没选区（选中 {plain.SelectionCount} 行） {without}");

        Check("这一簇里确实只有一条被选中", chosen.SelectionCount == 1);
        Check("合并之后是一整块（横线连成一段）", without.Length > 0 && !without.Contains(';'));
        Check("选区不改变合并图形的形状（逐字相同）", withSelection == without);

        // ② 实机那一步：选中一整块，拖到另一块附近松手，落定之后必须仍然是一整块。
        var first = new List<LyricLine>
        {
            new() { Text = "甲一", Start = TimeSpan.FromSeconds(30) },
            new() { Text = "甲二", Start = TimeSpan.FromSeconds(30.05) },
            new() { Text = "甲三", Start = TimeSpan.FromSeconds(30.10) },
        };

        var second = new List<LyricLine>
        {
            new() { Text = "乙一", Start = TimeSpan.FromSeconds(33) },
            new() { Text = "乙二", Start = TimeSpan.FromSeconds(33.05) },
            new() { Text = "乙三", Start = TimeSpan.FromSeconds(33.10) },
        };

        var lyrics = new List<LyricLine>();
        lyrics.AddRange(first);
        lyrics.AddRange(second);

        var view = NewTrack(scene, lyrics);
        view.PixelsPerSecond = Near;
        view.ScrollSeconds = 29;

        var clusters = new List<LyricMarkCluster>();
        LyricTrackView.GroupInto(clusters, lyrics, 29, Near, LyricTrackView.LabelMinGap);

        Check("拖动之前是两块", clusters.Count == 2);

        var topY = clusters[0].TopY;
        IReadOnlyList<LyricTimeTarget>? reported = null;
        view.TimesChanged += (_, e) => reported = e.Targets;

        // 往下拖 40 像素 ≈ 2.17 秒，正好让两段挨到一起。
        view.BeginGesture(new Point(WaveformColumnWidth + 60, topY), KeyModifiers.None);
        view.ContinueGesture(new Point(WaveformColumnWidth + 60, topY + 40));
        view.EndGesture();

        // 照视图模型的做法落定：算出前后值 → 做成一步可撤销的改动 → 重新发布。
        var plan = LyricTimeline.Plan(reported!, lyrics, TimeSpan.FromSeconds(0.02));
        new TimeShiftCommand(plan.Changes).Apply();
        lyrics.Sort(static (left, right) => Nullable.Compare(left.Start, right.Start));
        view.Lyrics = [.. lyrics];

        LyricTrackView.GroupInto(clusters, lyrics, 29, Near, LyricTrackView.LabelMinGap);

        var after = BandSignature(RasterizeToBitmap(view, scene, 1.0));
        var reference = BandSignature(RasterizeToBitmap(NewTrack(scene, lyrics, Near, 29), scene, 1.0));

        Console.WriteLine(
            $"  拖走一块之后落定：{plan.Report.Changed} 行改了时间码，{clusters.Count} 簇"
            + $"（选中 {view.SelectionCount} 行）");
        Console.WriteLine($"    有选区 {after}");
        Console.WriteLine($"    没选区 {reference}");

        var same = after == reference;

        Check("拖过去之后与目标合成一簇", clusters.Count == 1);
        Check("落定之后仍是一整块（没有散成一条条线）", same && ParseRuns(reference).Count == 1);
        Check("落定之后选区还在（不需要先取消选择）", view.SelectionCount == 3);

        Report();
    }

    /// <summary>造一个只有歌词标识层的控件，用于形状对比。</summary>
    private static LyricTrackView NewTrack(Size scene, IReadOnlyList<LyricLine> lyrics,
                                           double pixelsPerSecond = 40, double scrollSeconds = 0)
    {
        var track = new LyricTrackView
        {
            Width = scene.Width,
            Height = scene.Height,
            TextInset = WaveformColumnWidth,
            Lyrics = lyrics,
            MinimumSeparation = TimeSpan.FromSeconds(0.02),
            PixelsPerSecond = pixelsPerSecond,
            ScrollSeconds = scrollSeconds,
        };

        Layout(track, scene);
        return track;
    }

    /// <summary>
    /// 复制一份歌词行。
    /// </summary>
    /// <remarks>
    /// 拖动落定是就地改行对象的（那是产品里的真实做法），所以要把「会改数据的那一幕」
    /// 与「给测量用的那一份」分开。共用一份的话，出图顺序就会悄悄改变测量的输入——
    /// 踩过一次，表现是后面那一段测量整段跳过，而跳过只是因为一条长句被挪走了。
    /// </remarks>
    private static List<LyricLine> Clone(IReadOnlyList<LyricLine> lyrics) =>
        [.. lyrics.Select(static line => new LyricLine { Text = line.Text, Start = line.Start, End = line.End })];

    /// <summary>
    /// 在控件上做一次完整的「按下 → 拖动 → 松手 → 落定」，照视图模型那一条路走。
    /// </summary>
    /// <remarks>
    /// 落定这一步刻意不省：控件只负责报出「应该落到哪儿」，真正改时间码、错开、重新发布
    /// 都在数据层。出图时要看到的是「松手之后的样子」，所以这一段必须真的跑一遍，
    /// 而不是把控件摆成拖动中的姿势就算了。
    /// </remarks>
    private static void DropSelection(LyricTrackView track, double fromY, double toY)
    {
        const double x = WaveformColumnWidth + 60;

        IReadOnlyList<LyricTimeTarget>? reported = null;
        track.TimesChanged += (_, e) => reported = e.Targets;

        track.BeginGesture(new Point(x, fromY), KeyModifiers.None);
        track.ContinueGesture(new Point(x, toY));
        track.EndGesture();

        if (reported is null) return;

        var lines = track.Lyrics!.ToList();
        var plan = LyricTimeline.Plan(reported, lines, TimeSpan.FromSeconds(0.02));

        new TimeShiftCommand(plan.Changes).Apply();

        lines.Sort(static (left, right) => Nullable.Compare(left.Start, right.Start));

        // 行对象没换，所以选区原样保住——这正是「不需要先取消选择」的那件事。
        track.Lyrics = lines;
    }

    private static void Layout(Layoutable view, Size size)
    {
        view.Measure(size);
        view.Arrange(new Rect(size));
    }

    /// <summary>
    /// 记一条断言。
    /// </summary>
    /// <remarks>
    /// 上面那些测量报的是数字（看数值合不合理），这一组报的是判定（行为对不对）。
    /// 交互类的东西一旦错了不会崩、也不会难看到一眼发现，所以值得直接给出「通过 / 失败」，
    /// 而不是留一串需要人去心算的数字。
    /// </remarks>
    private static void Check(string what, bool ok)
    {
        _checks++;

        if (ok) return;

        _failed++;
        Console.WriteLine($"  × {what}");
    }

    private static void Report()
    {
        Console.WriteLine($"  检查 {_checks} 条，失败 {_failed} 条");
        Console.WriteLine();
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
