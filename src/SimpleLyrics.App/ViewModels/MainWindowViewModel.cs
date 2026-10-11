using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleLyrics.Core.Audio;
using SimpleLyrics.Core.Editing;
using SimpleLyrics.Core.Lyrics;
using SimpleLyrics.Core.Peaks;

namespace SimpleLyrics.App.ViewModels;

/// <summary>
/// 右栏「文件信息」里的一行。
/// </summary>
/// <param name="IsLyrics">
/// 这一行描述的是歌词而不是音频。载入新音频时整块 <c>Details</c> 都会被清空重建，
/// 歌词却是另一条独立的导入路径，重建后要靠这个标志把歌词那几行放回去。
/// </param>
public sealed record InfoRow(string Label, string Value, bool IsLyrics = false);

/// <summary>
/// 主窗口的视图模型。
/// </summary>
/// <remarks>
/// <para>
/// 界面上的「播放头位置」由 <see cref="PlayStartSeconds"/> 表达——它由波形控件算出
/// （顶端时刻加上指示器比例对应的那一段），因为这个换算需要控件高度，
/// 视图模型拿不到。于是数据流是：控件报告位置 → 这里决定要不要铺缓存或跳转。
/// </para>
/// <para>
/// 播放过程中位置是反着流的：这里的 <see cref="PlayheadSeconds"/> 被设备时钟持续刷新，
/// 控件据此滚动波形。两者都写同一个属性，靠 <see cref="_suppressPlayhead"/> 区分
/// 「我方刷新」与「用户挪动了播放头」。
/// </para>
/// <para>
/// 对话框之类依赖平台能力的交互留在 View 里，视图模型只处理状态。
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 手动改变播放位置之后，等这么久才真正开始铺缓存。
    /// </summary>
    /// <remarks>
    /// 拖动指示器和滚动波形都是连续动作，每一帧都会产生一个新位置。若立刻响应，
    /// 就会排出一串半途而废的解码任务，既浪费解码又让界面卡顿。
    /// 等手停下来再动手，实际只解用户最终停留的那一处。
    /// </remarks>
    private static readonly TimeSpan PrepareDebounce = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 播放位置的刷新间隔。
    /// </summary>
    /// <remarks>
    /// 取 8 毫秒（约 120 Hz）而不是贴着 60 Hz 的 16 毫秒：定时器节拍与渲染节拍之间
    /// 总会有些漂移，卡在同一个频率上就会周期性错过某一帧。刷新得比渲染快一档，
    /// 每一帧就都能拿到最新位置。真正的重绘仍由 Avalonia 按自己的帧率决定，
    /// 且控件里有半像素死区挡着，提高刷新率并不会成比例地增加绘制量。
    /// </remarks>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(8);

    private readonly DispatcherTimer _prepareTimer;
    private readonly DispatcherTimer _tickTimer;

    private AudioPlayer? _player;
    private string? _audioPath;
    private long _totalFrames;
    private double _pendingPrepareSeconds;
    private bool _suppressPlayhead;

    /// <summary>
    /// 播放头被用户挪到过别处。
    /// </summary>
    /// <remarks>
    /// 用来区分「原地点播放」与「挪了位置再点播放」：前者可以从暂停处平滑继续
    /// （不重置设备缓冲，听不到咔嗒），后者必须按新位置重新定位，
    /// 否则用户滚动波形看到的位移等于白做——一按播放就跳回原位。
    /// </remarks>
    private bool _playheadRelocated;

    private bool _disposed;

    public MainWindowViewModel()
    {
        _prepareTimer = new DispatcherTimer { Interval = PrepareDebounce };
        _prepareTimer.Tick += OnPrepareTimerTick;

        // 用 Render 优先级让位置更新紧贴渲染节拍。默认的 Normal 与渲染之间没有对齐关系，
        // 会出现某一帧更新两次、下一帧一次也没有——看上去就是波形一抽一抽的。
        _tickTimer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, OnTickTimerTick);
    }

    // ------------------------------------------------------------ 文件

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudio))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private PeakPyramid? _peaks;

    [ObservableProperty]
    private string _fileName = "未载入音频";

    [ObservableProperty]
    private string _statusText = "就绪 —— 点击「打开音频…」或按 Ctrl+O";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    /// <summary>波形滚动位置（秒）。与 <c>WaveformView</c> 双向绑定。</summary>
    [ObservableProperty]
    private double _scrollSeconds;

    /// <summary>波形纵向缩放（每秒钟占多少像素）。与 <c>WaveformView</c> 双向绑定。</summary>
    [ObservableProperty]
    private double _pixelsPerSecond = 40;

    /// <summary>右栏展示的文件信息。</summary>
    public ObservableCollection<InfoRow> Details { get; } = [];

    /// <summary>改动历史。撤销 / 重做只认它，见 <see cref="TimelineEditHistory"/>。</summary>
    private readonly TimelineEditHistory _history = new();

    /// <summary>上次保存（或刚载入）时历史停在哪儿；与当前位置不同就说明有未保存的改动。</summary>
    private int _savedPosition;

    // ------------------------------------------------------------ 歌词

    /// <summary>当前载入的歌词文档（含未打轴的行）。编辑功能就位前只作展示与统计。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLyrics))]
    private LyricDocument? _lyrics;

    /// <summary>
    /// 已打轴的歌词行，按时间升序。绑定给波形上的歌词标识层。
    /// </summary>
    /// <remarks>
    /// 单独暴露一份，而不是让控件自己去 <see cref="Lyrics"/> 里筛选：
    /// 控件每次重绘都要用它，而筛选与排序是有成本的；更重要的是「按时间升序」是
    /// 标识层分簇算法的前提，把它交给数据层保证，比让控件每次自己排一遍可靠。
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<LyricLine> _timedLyrics = [];

    /// <summary>歌词栏的标题。</summary>
    [ObservableProperty]
    private string _lyricsCaption = "已绑定歌词";

    /// <summary>尚未导入歌词时歌词栏里的提示。</summary>
    [ObservableProperty]
    private string _lyricsHint = "尚未导入歌词\n\n点击工具栏的「导入歌词…」\n或按 Ctrl+I 载入 .lrc 文件";

    /// <summary>
    /// 两条歌词之间至少留出的时间；拖到更近处会被自动错开。
    /// </summary>
    /// <remarks>
    /// 由窗口那一侧从「最大放大倍率」算出来送进来（<c>LyricTrackView.MinimumSeparationFor</c>）：
    /// 这个值取决于素材的峰值桶有多细（采样率）、以及显示层的合并门槛，
    /// 视图模型两样都拿不到，自己推一遍只会推出第二份真相。
    /// </remarks>
    [ObservableProperty]
    private TimeSpan _minimumLyricSeparation;

    partial void OnMinimumLyricSeparationChanged(TimeSpan value) => RefreshLyricsDetails(Lyrics);

    /// <summary>
    /// 歌词里有没有未保存的改动。
    /// </summary>
    /// <remarks>
    /// 有未保存改动时不许换音频：换曲要清掉整份歌词（旧时间轴对新曲子毫无意义），
    /// 而那等于把用户刚拖出来的改动一起丢掉。没有撤销栈，丢了就是真丢了。
    /// </remarks>
    public bool HasUnsavedLyrics => Lyrics is { IsDirty: true };

    /// <summary>挡下「换音频」时给用户的那句话。界面层与视图模型共用同一份措辞。</summary>
    public const string UnsavedLyricsHint =
        "歌词有未保存的改动 —— 先按 Ctrl+S 保存，或按 Ctrl+Shift+D 放弃改动（然后重新打开音频）";

    // ------------------------------------------------------------ 播放

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playButtonText = "播放";

    /// <summary>
    /// 播放头所在时刻（秒）。<see cref="double.NaN"/> 表示尚未开始播放。
    /// </summary>
    [ObservableProperty]
    private double _playheadSeconds = double.NaN;

    /// <summary>波形控件算出的播放头时刻。它是缓存的准备目标，也是播放的起点。</summary>
    [ObservableProperty]
    private double _playStartSeconds;

    /// <summary>已解码缓存区间的起始时刻（秒）。</summary>
    [ObservableProperty]
    private double _cacheStartSeconds = double.NaN;

    /// <summary>已解码缓存区间的结束时刻（秒）。</summary>
    [ObservableProperty]
    private double _cacheEndSeconds = double.NaN;

    /// <summary>播放头还没铺好缓存。此时播放按钮应当禁用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanTogglePlayback))]
    private bool _isPreparing;

    [ObservableProperty]
    private string _positionText = "—";

    public bool HasAudio => Peaks is not null;

    /// <summary>是否已经导入歌词。</summary>
    public bool HasLyrics => Lyrics is not null;

    public string DurationText => Peaks is null ? "—" : FormatDuration(Peaks.Duration);

    /// <summary>播放按钮是否可用。缓存没铺到播放头时必须禁用——按下去也没东西可放。</summary>
    public bool CanPlay => HasAudio && !IsBusy && !IsPreparing;

    /// <summary>
    /// 播放键（按钮与空格）是否可用。
    /// </summary>
    /// <remarks>
    /// 播放中必须保持可用：否则缓存一旦暂时没跟上就把按钮变灰，
    /// 用户会连暂停都按不了，只能眼睁睁看着它播完。
    /// </remarks>
    public bool CanTogglePlayback => HasAudio && !IsBusy && (IsPlaying || !IsPreparing);

    // ------------------------------------------------------------ 载入

    /// <summary>
    /// 解码一个音频文件、显示其波形，并准备播放缓存。
    /// </summary>
    public async Task LoadAsync(string path)
    {
        if (IsBusy) return;

        // 换曲必须清掉歌词，而歌词里可能正躺着用户刚拖出来的改动。
        // 界面层在弹文件对话框之前也会挡一道，这里再挡是为了让「不许覆盖」这件事
        // 由持有文档的这边说了算，而不是靠调用方记得。
        if (HasUnsavedLyrics)
        {
            StatusText = UnsavedLyricsHint;
            return;
        }

        DisposePlayer();

        IsBusy = true;
        Progress = 0;
        FileName = Path.GetFileName(path);
        StatusText = $"正在解码 {FileName} …";

        // .lrc 是音频的侧车文件：换了曲子，上一首的时间轴就不再指向任何东西。
        ClearLyrics();

        try
        {
            // Progress<T> 在构造时捕获 UI 线程的同步上下文，因此 Report 会自动切回 UI 线程。
            var progress = new Progress<double>(value => Progress = value);

            // 4 分钟素材实测约 0.2 秒，但仍然放到线程池上跑，避免以后遇到长文件时界面僵住。
            var result = await Task.Run(() => PeakGenerator.Generate(path, progress));

            Peaks = result.Pyramid;
            _audioPath = path;
            _totalFrames = result.Pyramid.TotalFrames;
            FillDetails(result);

            StatusText = $"已载入 {FileName} · 权威时长 {FormatDuration(result.Pyramid.Duration)}"
                       + $" · 解码耗时 {result.Elapsed.TotalMilliseconds:F0} ms";
        }
        catch (Exception ex)
        {
            Peaks = null;
            Details.Clear();
            StatusText = $"载入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            UpdateReadiness();
        }

        // 播放器单独一步：它要再打开一次解码器，失败也不该把已经出来的波形一起废掉。
        await EnsurePlayerAsync();
    }

    /// <summary>
    /// 建立播放器并开始铺曲首附近的缓存。
    /// </summary>
    /// <remarks>
    /// 刻意放在载入流程之后、并且不阻塞它：播放器构造要打开解码器（几十毫秒），
    /// 而这段等待对「打开文件就能看到波形」毫无贡献。
    /// </remarks>
    private async Task EnsurePlayerAsync()
    {
        var path = _audioPath;
        if (path is null || _player is not null) return;

        var frames = _totalFrames;

        try
        {
            var player = await Task.Run(() => new AudioPlayer(path, frames));

            // 等待期间用户可能又打开了别的文件，那样这个播放器对应的就不是当前音频了。
            if (_disposed || !ReferenceEquals(_audioPath, path))
            {
                player.Dispose();
                return;
            }

            player.CacheChanged += OnPlayerCacheChanged;
            player.StateChanged += OnPlayerStateChanged;
            player.PlaybackEnded += OnPlayerPlaybackEnded;

            _player = player;

            RefreshCacheState();
            RequestPrepare(0);
        }
        catch (Exception ex)
        {
            StatusText = $"无法初始化播放：{ex.Message}";
        }
    }

    private void DisposePlayer()
    {
        _prepareTimer.Stop();
        _tickTimer.Stop();

        var player = _player;
        _player = null;

        if (player is not null)
        {
            player.CacheChanged -= OnPlayerCacheChanged;
            player.StateChanged -= OnPlayerStateChanged;
            player.PlaybackEnded -= OnPlayerPlaybackEnded;
            player.Dispose();
        }

        _audioPath = null;
        _totalFrames = 0;

        IsPlaying = false;
        PlayButtonText = "播放";
        IsPreparing = false;
        _playheadRelocated = false;
        PlayheadSeconds = double.NaN;
        CacheStartSeconds = double.NaN;
        CacheEndSeconds = double.NaN;
        PositionText = "—";
    }

    // ------------------------------------------------------------ 歌词

    /// <summary>
    /// 导入一份已经打好轴的 <c>.lrc</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意做成同步：歌词文件通常只有几 KB，解析耗时在微秒量级，
    /// 为它走一趟线程池与 <c>await</c> 反而多出一次上下文切换。
    /// </para>
    /// <para>
    /// 失败时只更新状态栏，不动已经载入的音频，也不清掉上一次的歌词——
    /// 「导入了一份坏文件」不该把先前的工作成果一起抹掉。
    /// </para>
    /// </remarks>
    public void LoadLyrics(string path)
    {
        try
        {
            var document = LrcParser.Load(path, out var warning);
            document.SourcePath = path;

            var timed = document.TimedLines;
            var untimed = document.UntimedLines;

            // 换了一份歌词，上一条改动历史里的行对象全都不再属于当前文档。
            _history.Clear();
            _savedPosition = 0;

            Lyrics = document;
            TimedLyrics = timed;
            UpdateLyricsCaption();

            var parts = new List<string> { $"已导入 {Path.GetFileName(path)}" };

            parts.Add(timed.Count > 0 ? $"{timed.Count} 行带时间戳" : "没有带时间戳的行");
            if (untimed.Count > 0) parts.Add($"{untimed.Count} 行未打轴（打轴环节尚未实现）");

            // offset 只提示、不折算，理由见 LrcMetadata.OffsetMilliseconds。
            // 这种事必须说出来：不说的话，用户会以为时间轴与播放对不上是本工具的毛病。
            if (document.Metadata.OffsetMilliseconds != 0)
            {
                parts.Add($"文件声明 offset {document.Metadata.OffsetMilliseconds:+#;-#;0} ms，"
                        + "本工具按原始时间戳显示、未折算");
            }

            if (warning is not null) parts.Add(warning);

            StatusText = string.Join(" · ", parts);
            RefreshLyricsDetails(document);
        }
        catch (Exception ex)
        {
            StatusText = $"导入歌词失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 音频换了一个文件，把上一首的歌词清掉。
    /// </summary>
    /// <remarks>
    /// <c>.lrc</c> 是音频的侧车文件，换了曲子之后旧歌词的时间轴毫无意义，
    /// 留着只会在新波形上画出一堆对不上的线。这条路径只在
    /// <see cref="HasUnsavedLyrics"/> 为假时才会走到——见 <see cref="LoadAsync"/>。
    /// </remarks>
    private void ClearLyrics()
    {
        _history.Clear();
        _savedPosition = 0;

        Lyrics = null;
        TimedLyrics = [];
        LyricsCaption = "已绑定歌词";
        RefreshLyricsDetails(null);
    }

    /// <summary>
    /// 把拖动产生的目标时刻写进文档，与别的歌词挨得太近的自动错开。
    /// </summary>
    /// <remarks>
    /// 拖动的全过程里时间轴数据一个字都没改，改动全都发生在松手这一下——
    /// 界面层因此不必在拖动期间反复重建绑定的列表，而「拖动中不重算合并与省略」
    /// 也成了免费的：那些判据只取决于时间码，时间码没动，它们算出来就还是原样。
    /// </remarks>
    public void ApplyTimeShift(IReadOnlyList<LyricTimeTarget> targets)
    {
        var document = Lyrics;
        if (document is null || targets.Count == 0) return;

        var plan = LyricTimeline.Plan(targets, document.TimedLines, MinimumLyricSeparation);

        if (plan.Changes.Count == 0)
        {
            // 拖回原位、或者根本没挪动。不动数据也不占一步撤销——
            // 否则用户按一次 Ctrl+Z 会看起来毫无反应。
            StatusText = "这几行没有移动，时间码没有变化";
            return;
        }

        _history.Push(new TimeShiftCommand(plan.Changes));
        PublishLyrics();

        var parts = new List<string> { $"已调整 {plan.Report.Changed} 行歌词的时间码" };

        if (plan.Report.Nudged > 0)
        {
            parts.Add($"{plan.Report.Nudged} 行与相邻歌词过近，已自动错开"
                    + $"（最大 {plan.Report.LargestNudge.TotalMilliseconds:F0} 毫秒）");
        }

        parts.Add("Ctrl+Z 撤销 · Ctrl+S 保存");
        StatusText = string.Join(" · ", parts);
    }

    /// <summary>
    /// 撤销一步。
    /// </summary>
    /// <remarks>
    /// 撤销只认 <see cref="IEditCommand"/>，不认「这是拖动还是盖章」——将来粘贴、盖章、
    /// 批量偏移各自做成一个命令之后，这里一个字都不用改。见 <see cref="TimelineEditHistory"/>。
    /// </remarks>
    public void Undo()
    {
        var label = _history.Undo();

        if (label is null)
        {
            StatusText = "没有可撤销的改动";
            return;
        }

        PublishLyrics();
        StatusText = $"已撤销：{label}";
    }

    /// <summary>重做一步。</summary>
    public void Redo()
    {
        var label = _history.Redo();

        if (label is null)
        {
            StatusText = "没有可重做的改动";
            return;
        }

        PublishLyrics();
        StatusText = $"已重做：{label}";
    }

    /// <summary>
    /// 改动落定之后统一收尾：作废派生缓存、重新发布、刷新「有没有未保存的改动」。
    /// </summary>
    /// <remarks>
    /// <see cref="LyricDocument.IsDirty"/> 由历史的位置推出来，而不是设成一个一劳永逸的
    /// 布尔标志：这样「改一笔又撤回来」不会留下一个假的未保存标记——按标志位做的话，
    /// 撤销回去它也不知道该把标记清掉，于是「关闭前提示」会一直误报。
    /// </remarks>
    private void PublishLyrics()
    {
        var document = Lyrics;
        if (document is null) return;

        // 时间码是就地改在行对象上的，排序缓存不会自己知道；不通知它，
        // 屏幕上就是「时间改了、位置没动」——而且下一条改动还会拿着旧顺序继续算。
        document.Invalidate();
        document.IsDirty = _history.Position != _savedPosition;

        TimedLyrics = document.TimedLines;
        UpdateLyricsCaption();
        RefreshLyricsDetails(document);
    }

    /// <summary>
    /// 把歌词写到指定路径。
    /// </summary>
    /// <remarks>
    /// <b>「有源文件」与「没有源文件」在这里没有分支。</b>路径由界面层决定——有源文件时
    /// 就用 <see cref="LyricDocument.SourcePath"/>，没有时问用户要一个——这个方法只管写。
    /// 写完之后 <see cref="LyricDocument.SourcePath"/> 就指了过去，于是将来
    /// 「从零创建歌词」在第一次保存之后与导入来的歌词再无区别。
    /// 落盘、改正来源、清掉未保存标记这三件事收在 <see cref="LrcWriter.Save"/> 一处。
    /// </remarks>
    public bool SaveLyrics(string path)
    {
        var document = Lyrics;

        if (document is null)
        {
            StatusText = "还没有载入歌词，没有可保存的内容";
            return false;
        }

        try
        {
            var bytes = LrcWriter.Save(document, path);

            // 保存点就是历史当前的位置：此后「改一笔再撤回」不会再显示未保存。
            _savedPosition = _history.Position;

            UpdateLyricsCaption();
            RefreshLyricsDetails(document);

            var timed = document.TimedLines.Count;
            var untimed = document.UntimedLines.Count;
            var content = untimed > 0 ? $"{timed} 行已绑定 · {untimed} 行未绑定" : $"{timed} 行已绑定";

            StatusText = $"已保存 {Path.GetFileName(path)} · {content} · {bytes / 1024.0:F1} KB";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 丢掉未保存的改动：按来源文件重新读一遍。
    /// </summary>
    /// <remarks>
    /// 没有撤销栈时这是唯一的退路，所以它得存在且要好按。刻意做成「重新读文件」
    /// 而不是「清空歌词」：用户丢掉的是刚才那几下拖动，不是整份歌词。
    /// </remarks>
    public void DiscardLyricEdits()
    {
        var path = Lyrics?.SourcePath;

        if (path is null || !File.Exists(path))
        {
            ClearLyrics();
            StatusText = "已放弃歌词改动（这份歌词没有来源文件，改动已整体丢弃）";
            return;
        }

        LoadLyrics(path);
        StatusText = "已放弃未保存的改动 —— 按来源文件重新载入";
    }

    /// <summary>工具栏上那句「已绑定歌词 · N 行」，顺带把未保存状态说出来。</summary>
    private void UpdateLyricsCaption()
    {
        var document = Lyrics;

        if (document is null)
        {
            LyricsCaption = "已绑定歌词";
            return;
        }

        var text = $"已绑定歌词 · {document.TimedLines.Count} 行";

        // 保存键是 Ctrl+S，但没有菜单也没有按钮，所以「有没有未保存的东西」
        // 只能靠这行字说。不说的话，用户不会知道自己该按什么。
        if (document.IsDirty) text += " · 未保存（Ctrl+S）";

        LyricsCaption = text;
    }

    /// <summary>把歌词那几行信息放进右栏，先摘掉上一次的。</summary>
    private void RefreshLyricsDetails(LyricDocument? document)
    {
        for (var index = Details.Count - 1; index >= 0; index--)
        {
            if (Details[index].IsLyrics) Details.RemoveAt(index);
        }

        if (document is null) return;

        var path = document.SourcePath;
        var timed = document.TimedLines.Count;
        var untimed = document.UntimedLines.Count;

        var summary = untimed > 0
            ? $"{timed} 行已绑定 · {untimed} 行未绑定 · 共 {document.Lines.Count} 行"
            : $"{timed} 行已绑定 · 共 {document.Lines.Count} 行";

        if (path is null) summary += "\n（尚无来源文件，第一次保存时会询问保存位置）";
        else summary += document.IsDirty ? $"\n{Path.GetFileName(path)}（有未保存的改动）" : $"\n{Path.GetFileName(path)}";

        if (document.Metadata.Title is { Length: > 0 } title) summary += $"\n标题：{title}";
        if (document.Metadata.Artist is { Length: > 0 } artist) summary += $"\n艺术家：{artist}";

        Details.Add(new InfoRow("歌词", summary, IsLyrics: true));

        if (MinimumLyricSeparation > TimeSpan.Zero)
        {
            var units = LyricTimeline.MinimumUnits(MinimumLyricSeparation);

            Details.Add(new InfoRow(
                "歌词最小间隔",
                $"{units * 10} 毫秒\n最大放大倍率下两条标识线不合并的最小距离"
                + $"（理论上限 {MinimumLyricSeparation.TotalMilliseconds:F2} 毫秒，"
                + "按 LRC 的 10 毫秒时间码向上取整）",
                IsLyrics: true));
        }

        if (document.Metadata.OffsetMilliseconds != 0)
        {
            Details.Add(new InfoRow(
                "歌词 offset",
                $"{document.Metadata.OffsetMilliseconds:+#;-#;0} ms（文件声明；显示与导出均未折算）",
                IsLyrics: true));
        }
    }

    /// <summary>还没有源文件时的建议保存位置：与音频同名、同目录的 <c>.lrc</c>。</summary>
    public string? SuggestedLyricPath => LrcWriter.SuggestPath(_audioPath);

    // ------------------------------------------------------------ 播放控制

    /// <summary>播放 / 暂停 / 继续。</summary>
    /// <remarks>
    /// 暂停与停止在「从哪儿开始放」这一点上是同一回事：都由<b>指示条当前所指的时刻</b>决定。
    /// 唯一的差别是原地点播放可以从暂停处平滑继续，不必重置设备缓冲，
    /// 所以位置没被动过时仍然走 <see cref="AudioPlayer.Resume"/>。
    /// </remarks>
    public void TogglePlayPause()
    {
        var player = _player;
        if (player is null || IsBusy) return;

        if (player.State == AudioPlaybackState.Playing)
        {
            player.Pause();
            SyncPlaybackState();
            return;
        }

        // 位置没被动过才能原地继续。
        if (player.State == AudioPlaybackState.Paused && !_playheadRelocated)
        {
            player.Resume();
            SyncPlaybackState();
            return;
        }

        var target = PlayStartSeconds;

        if (!player.Covers(target))
        {
            // 还没铺到那里。先准备，不自动开始——用户按下的是播放键，
            // 但此刻放不出来，与其放一段静音不如明确告诉他正在准备。
            RequestPrepare(target);
            StatusText = $"正在准备 {FormatDuration(TimeSpan.FromSeconds(target))} 附近的音频…";
            return;
        }

        player.Play(target);
        _playheadRelocated = false;
        SyncPlaybackState();
    }

    partial void OnPlayheadSecondsChanged(double value)
    {
        // 自己刷新播放位置时不算用户操作。
        if (_suppressPlayhead) return;

        var player = _player;
        if (player is null || player.State != AudioPlaybackState.Playing) return;
        if (!double.IsFinite(value)) return;

        if (player.Covers(value))
        {
            // 缓存里就有：直接跳，播放不中断。
            player.Play(value);
            RefreshCacheState();
            return;
        }

        // 缓存之外：刹车是立刻的，铺路要等手停下来。
        //
        // 之所以立刻刹：用户已经听不到他指定的内容了，让他继续听下去毫无意义；
        // 而且按钮必须马上变灰，否则他会以为按钮坏了。播放已经停下，
        // 重新解码的耗时不再触碰任何实时性约束，所以缓存可以放心从头重来。
        //
        // 之所以不立刻解：这一下可能只是拖动途中的某一帧，等一秒往往就跳过去了。
        player.Halt(value);
        SyncPlaybackState();
        UpdateReadiness();
        SchedulePrepare(value);
        StatusText = $"正在准备 {FormatDuration(TimeSpan.FromSeconds(value))} 附近的音频…";
    }

    partial void OnPlayStartSecondsChanged(double value)
    {
        // 播放中播放头由设备时钟驱动，滚动产生的这个值只是中间产物，不该再触发铺缓存。
        if (IsPlaying) return;
        if (!double.IsFinite(value)) return;

        // 暂停或停止时这个值只可能因用户动作而改变：滚动波形、拖动指示条、缩放、复位。
        // 播放中那条路径已被上面的判断挡掉，而暂停时没有任何东西会自发去写它。
        // 于是这里可以放心断定「用户把播放头挪到别处了」。
        _playheadRelocated = true;

        SchedulePrepare(value);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (value) _tickTimer.Start();
        else _tickTimer.Stop();
    }

    private void OnTickTimerTick(object? sender, EventArgs e)
    {
        var player = _player;
        if (player is null || player.State != AudioPlaybackState.Playing)
        {
            _tickTimer.Stop();
            return;
        }

        var position = player.PositionSeconds;
        SetPlayhead(position);

        // 只精确到 0.1 秒：这个文本每变一次都要走一轮绑定与布局，
        // 毫秒级的话每帧都在变，等于给渲染又加了一份固定开销。
        PositionText = $"{FormatPosition(TimeSpan.FromSeconds(position))} / {DurationText}";
    }

    private void SetPlayhead(double seconds)
    {
        _suppressPlayhead = true;
        try
        {
            PlayheadSeconds = seconds;
        }
        finally
        {
            _suppressPlayhead = false;
        }
    }

    // ------------------------------------------------------------ 缓存准备

    private void OnPrepareTimerTick(object? sender, EventArgs e)
    {
        _prepareTimer.Stop();
        RequestPrepare(_pendingPrepareSeconds);
    }

    private void SchedulePrepare(double seconds)
    {
        _pendingPrepareSeconds = seconds;
        _prepareTimer.Stop();
        _prepareTimer.Start();
    }

    private void RequestPrepare(double seconds)
    {
        // 立即准备意味着先前排队的延迟准备已经多余，留着只会在一秒后白解一次。
        _prepareTimer.Stop();

        var player = _player;
        if (player is null) return;

        player.Prepare(seconds);
        RefreshCacheState();
    }

    private void OnPlayerCacheChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshCacheState);

    private void OnPlayerStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncPlaybackState);

    private void OnPlayerPlaybackEnded(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(HandlePlaybackEnded);

    private void RefreshCacheState()
    {
        var player = _player;

        if (player is null)
        {
            CacheStartSeconds = double.NaN;
            CacheEndSeconds = double.NaN;
            IsPreparing = false;
            return;
        }

        CacheStartSeconds = player.CacheStartSeconds;
        CacheEndSeconds = player.CacheEndSeconds;

        // 解码失败时不能把按钮永久锁死——此后不会再有事件来解除它。
        if (player.Failure is { } failure)
        {
            IsPreparing = false;
            StatusText = $"解码失败：{failure.Message}";
            return;
        }

        UpdateReadiness();
    }

    private void UpdateReadiness()
    {
        var player = _player;
        IsPreparing = player is not null && !player.Covers(PlayStartSeconds);
    }

    private void SyncPlaybackState()
    {
        var playing = _player?.State == AudioPlaybackState.Playing;

        IsPlaying = playing;
        PlayButtonText = playing ? "暂停" : "播放";

        UpdateReadiness();
    }

    private void HandlePlaybackEnded()
    {
        IsPlaying = false;
        PlayButtonText = "播放";
        StatusText = "播放结束";

        if (_player is { } player) SetPlayhead(player.DurationSeconds);
    }

    // ------------------------------------------------------------ 其它

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePlayer();
    }

    private void FillDetails(PeakGenerationResult result)
    {
        var pyramid = result.Pyramid;
        var info = result.Info;

        var speed = result.Elapsed.TotalSeconds > 1e-6
            ? pyramid.DurationSeconds / result.Elapsed.TotalSeconds
            : 0;

        Details.Clear();
        Details.Add(new InfoRow("文件名", Path.GetFileName(info.Path)));
        Details.Add(new InfoRow("编解码器", info.Summary));
        Details.Add(new InfoRow("权威时长", FormatDuration(pyramid.Duration)));
        Details.Add(new InfoRow("总帧数", $"{pyramid.TotalFrames:N0} 帧/声道"));
        Details.Add(new InfoRow(
            "峰值金字塔",
            $"{pyramid.Levels.Count} 级 · 最细 {PeakGenerator.BaseBucketFrames} 帧/桶 · "
            + $"内存 {pyramid.ApproximateBytes / 1024.0:F0} KB"));
        Details.Add(new InfoRow("解码性能", $"{result.Elapsed.TotalMilliseconds:F0} ms（约 {speed:F0} 倍实时）"));
        Details.Add(new InfoRow(
            "播放缓存",
            $"{PcmBlockStore.BlockFrames} 帧/块 · 立体声每块 {PcmBlockStore.BlockFrames * 8 / 1024} KB · "
            + $"上限随可用内存浮动，当前 {SystemMemory.CacheBudget() / 1024.0 / 1024.0:F0} MB"));
        Details.Add(new InfoRow(
            "FFmpeg 运行库",
            $"{FfmpegRuntime.RuntimeVersion ?? "未加载"}\n{FfmpegRuntime.LibraryPath ?? "—"}"));

        if (info.ContainerDuration > TimeSpan.Zero)
        {
            var drift = (info.ContainerDuration - pyramid.Duration).TotalMilliseconds;
            Details.Add(new InfoRow(
                "容器声明时长",
                $"{FormatDuration(info.ContainerDuration)}（与权威时长相差 {drift:F0} ms）"));
        }
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.fff") : value.ToString(@"mm\:ss\.fff");

    /// <summary>
    /// 播放位置的显示格式，只到 0.1 秒。
    /// </summary>
    /// <remarks>
    /// 刻意比 <see cref="FormatDuration"/> 粗糙一档：这个文本在播放中每秒要重写若干次，
    /// 每写一次都带走一轮绑定与布局，而毫秒位的跳动对「现在放到哪儿了」毫无信息量。
    /// </remarks>
    private static string FormatPosition(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.f") : value.ToString(@"mm\:ss\.f");
}
