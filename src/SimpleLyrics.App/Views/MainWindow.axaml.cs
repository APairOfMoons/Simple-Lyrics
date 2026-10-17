using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimpleLyrics.App.ViewModels;
using SimpleLyrics.Controls;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// 文件对话框里展示的格式。这里列的是 <b>FFmpeg 能解</b>的范围，
    /// 比 v1 承诺的 MP3/FLAC/AAC 更宽——多列几种不会出错，解不了时解码器会明确报错。
    /// </summary>
    private static readonly FilePickerFileType AudioFileType = new("音频文件")
    {
        Patterns =
        [
            "*.mp3", "*.flac", "*.m4a", "*.aac", "*.ogg", "*.opus",
            "*.wav", "*.wma", "*.ape", "*.wv", "*.mka",
        ],
    };

    private readonly MainWindowViewModel _viewModel = new();

    /// <summary>
    /// 歌词文件。同时收 <c>.txt</c>：很多人的歌词就是从记事本里存出来的，
    /// 存成什么扩展名全凭习惯，而格式对不对解析器自己会判断。
    /// </summary>
    private static readonly FilePickerFileType LyricFileType = new("LRC 歌词")
    {
        Patterns = ["*.lrc", "*.txt"],
    };

    /// <summary>
    /// 空格是否已经按下、还没有松开。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要它</b>：按住一个键超过系统的重复延迟（默认约 500 毫秒）之后，
    /// 系统会每隔几十毫秒重发一次 <c>KeyDown</c>。打轴时空格是高频操作，按下时间
    /// 很容易越过那个阈值，于是启停被连续切换两次而互相抵消——看起来就像键盘失灵。
    /// </para>
    /// <para>
    /// Avalonia 11.3.22 的 <c>KeyEventArgs</c> 没有暴露「这是自动重复」的标志
    /// （只有 Key / KeyModifiers / PhysicalKey / KeySymbol / KeyDeviceType），
    /// Win32 的「上一次按键状态」位没有透出来，所以只能自己记。
    /// </remarks>
    private bool _spaceHeld;

    /// <summary>
    /// 歌词栏里那次指针操作正握着哪个指针。
    /// </summary>
    /// <remarks>
    /// 拖动标识线时指针会跑出歌词栏（往上拖到波形栏乃至工具栏上都很正常），
    /// 所以按下之后必须捕获指针，否则移动事件会断在半路。捕获是<b>窗口这一侧</b>
    /// 借给标识层的：标识层自己不参与命中测试，拿到事件的是它下面那条 <c>Border</c>。
    /// 松手与取消时都要还回去，忘了的话整窗口的指针都会留在这一栏手里。
    /// </remarks>
    private IPointer? _lyricPointer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // 歌词标识层的文字从波形栏右侧开始，这个分界就是波形栏的实际宽度。
        // 从布局里量出来，而不是在 XAML 里再写一份列宽常量：那个数字已经写在
        // ColumnDefinitions 里，写两处迟早会改漏一处，而改漏的表现是歌词标签
        // 压在波形上——安静地难看，当场不容易发现。
        WaveformColumn.PropertyChanged += OnWaveformColumnPropertyChanged;

        // 换素材之后「最大放大倍率」会变，而最小间隔由它算出。
        Waveform.PropertyChanged += OnWaveformPropertyChanged;

        // 标识层只负责「怎么画、点到谁」，真正落到文档上的改动从这里出去。
        // 拖动期间一个事件都不会发——时间轴数据那时一个字都没改。
        LyricMarks.TimesChanged += (_, e) => _viewModel.ApplyTimeShift(e.Targets);
        LyricMarks.SelectionChanged += OnLyricSelectionChanged;

        // 「移出」箭头按下时把选中的那几行移出时间轴（压回「未绑定」栏）。
        // 要移出哪些行由标识层自己的选区给出，事件里不带快照——两份选区迟早会说法不一。
        LyricMarks.UnbindRequested += (_, _) => _viewModel.UnbindLyrics(LyricMarks.SelectedLines);

        // 用隧道阶段接住快捷键：冒泡阶段可能已被获得焦点的子控件消费掉，
        // 空格尤其如此——按钮拿到焦点后会把空格当成「点击」。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        // 失活时那个「松开」的事件不一定送得到，状态位会永久卡住，
        // 之后所有空格都会被当成自动重复而全部吞掉；拖动标识线也一样，
        // 留着不收的话这一栏会一直扣着指针捕获。
        Deactivated += (_, _) =>
        {
            _spaceHeld = false;

            if (LyricMarks.IsGestureActive) CancelLyricGesture();
        };
    }

    /// <summary>
    /// 波形栏宽度变化时同步歌词标识层的文字起点。
    /// </summary>
    /// <remarks>
    /// 订阅 <see cref="Visual.BoundsProperty"/> 而不是挂在窗口的尺寸变化上：波形栏的宽度
    /// 由列定义决定，窗口缩放、甚至将来加分隔条都会走同一条路径，盯住最终结果是唯一稳妥的。
    /// </remarks>
    private void OnWaveformColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) LyricMarks.TextInset = WaveformColumn.Bounds.Width;
    }

    private void OnWaveformPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WaveformView.PeaksProperty)
        {
            UpdateMinimumSeparation();

            // 「未绑定」栏那条线标的是「播放头在哪儿」，而没有音频就没有播放头：
            // 左栏此刻只显示一句提示，右栏却横着一条琥珀色的线，看上去像它标记着什么。
            // 判据取波形控件那一份（HasWaveform），两栏因此永远同进同退。
            UnboundLyrics.ShowIndicator = Waveform.HasWaveform;
        }

        // 「未绑定」栏里那条播放指示线是同一条线在右栏的延伸段：同一个高度比例、
        // 同样的粗细与颜色，三栏又同高，于是两栏的线严格对齐。
        // 走代码后置而不是 XAML 里的按名绑定（{Binding #Waveform.IndicatorRatio}），
        // 与 TextInset 是同一条理由：那种写法在沙箱里无法运行时验证，而失效的表现
        // 是两条线差出一截——安静地难看，只有开窗口才发现。
        if (e.Property == WaveformView.IndicatorRatioProperty) UnboundLyrics.IndicatorRatio = Waveform.IndicatorRatio;
    }

    /// <summary>
    /// 换素材之后重算「两条歌词之间至少留出的时间」。
    /// </summary>
    /// <remarks>
    /// 最小间隔 = 合并门槛 ÷ 最大放大倍率。前半由标识层持有（<c>LabelMinGap</c>），
    /// 后半由波形控件按素材的峰值精度算出（<c>MaximumZoom</c>）。两个判据各只有一处，
    /// 相除这一步放在这里——视图模型两样都拿不到，而控件层不该互相认识。
    /// </remarks>
    private void UpdateMinimumSeparation() =>
        _viewModel.MinimumLyricSeparation = LyricTrackView.MinimumSeparationFor(Waveform.MaximumZoom);

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;

        _spaceHeld = false;

        // KeyUp 也必须吃掉。
        //
        // 按钮是在「松开」这个阶段触发 Click 的，只在 KeyDown 阶段拦截的话，
        // 松开空格仍会落到当前获得焦点的按钮上——窗口刚打开时焦点默认在
        // 第一个可聚焦控件，也就是「打开音频…」，于是空格一边切换启停一边弹出文件对话框。
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                _ = PickAndLoadAsync();
                break;

            case Key.I when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                _ = PickAndImportLyricsAsync();
                break;

            case Key.T when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                _ = PickAndImportLyricTextAsync();
                break;

            // 保存键刻意要求「正好是 Ctrl」：Ctrl+Shift+S 是「另存为」的位置，留着。
            case Key.S when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _ = SaveLyricsAsync();
                break;

            case Key.D when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                e.Handled = true;
                _viewModel.DiscardLyricEdits();
                break;

            // 清空歌词。它是可撤销的一步（Ctrl+Z 能回来），所以不像别的破坏性操作那样
            // 需要先问一次——撤销栈已经在了，用它换掉一个对话框是最划算的。
            case Key.N when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                if (LyricMarks.IsGestureActive) CommitLyricGesture();
                _viewModel.ClearLyricsByUser();
                break;

            // 撤销 / 重做。正在拖动时先把这一拖落定——用户按 Ctrl+Z 想要的大半正是
            // 「把刚才那一下退回去」，而那时他手上还按着鼠标。
            case Key.Z when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                if (LyricMarks.IsGestureActive) CommitLyricGesture();
                _viewModel.Undo();
                break;

            case Key.Y when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                if (LyricMarks.IsGestureActive) CommitLyricGesture();
                _viewModel.Redo();
                break;

            // 拖到一半改主意了。没有撤销栈，所以「取消」这一下必须真的什么都不改——
            // 拖动期间时间轴数据确实一个字都没动，放弃就是把预览丢掉。
            case Key.Escape when LyricMarks.IsGestureActive:
                e.Handled = true;
                CancelLyricGesture();
                break;

            // 盖章：把「未绑定」栏栈顶那一行绑到此刻播放指示线所在的时刻。
            // Enter 与 F9 是 §8 定下的两个键，都收在这里。
            //
            // 与空格同一条坑：只拦 KeyDown 是不够的——松开时那个 Enter 仍会落到当前获得
            // 焦点的按钮上，把它当成一次点击，于是打轴的同时又弹出一个文件对话框。
            // 所以在隧道阶段吃掉并标记 Handled。
            //
            // 刻意不挡「自动重复」：按住 Enter 连着盖几句是打轴时很自然的用法，
            // 而每一句都会因为离上一条太近被自动错开（那种情况下最后都会挤在 20 毫秒的
            // 格子上排开，顺序不乱）。
            case Key.Enter or Key.F9 when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;

                // 拖动途中按下去：先把这一拖落定。用户要盖的是屏幕上正看着的那一份，
                // 而不是这次拖动开始之前的样子——与 Ctrl+S / Ctrl+Z 同一个口径。
                if (LyricMarks.IsGestureActive) CommitLyricGesture();

                _viewModel.StampTopLyric();
                break;

            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                // 无论能不能播都要吃掉这个按键，否则焦点所在的按钮会同时被触发一次。
                e.Handled = true;

                // 自动重复产生的后续 KeyDown 到此为止，不再切换启停。
                if (_spaceHeld) break;
                _spaceHeld = true;

                if (_viewModel.CanTogglePlayback) _viewModel.TogglePlayPause();
                break;
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => _ = PickAndLoadAsync();

    private void OnImportLyricsClick(object? sender, RoutedEventArgs e) => _ = PickAndImportLyricsAsync();

    private void OnPasteLyricsClick(object? sender, RoutedEventArgs e) => _ = PickAndImportLyricTextAsync();

    private void OnStampClick(object? sender, RoutedEventArgs e) => _viewModel.StampTopLyric();

    /// <summary>
    /// 鼠标滚轮在「已绑定歌词栏」上时，把事件转给波形控件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 歌词标识层本身不参与命中测试（它盖在波形之上，参与了就会把拖动、缩放、滚动整片挡掉），
    /// 所以滚轮落到的是它下面那条 Border，而 Border 自己什么也不做。两栏共用同一套时间轴参数，
    /// 用户在歌词上滚动时想要的显然是同一件事；不转发的话，滚轮往左移几十像素就有反应、
    /// 往右移几十像素就没反应，这种不一致最难解释。
    /// </para>
    /// <para>
    /// 只转发滚轮，不转发拖动：这一栏里的拖动已经被「拖动标识线改时间码」占用了。
    /// </para>
    /// </remarks>
    private void OnLyricColumnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Waveform.ApplyWheel(e.Delta.Y, e.KeyModifiers.HasFlag(KeyModifiers.Control))) e.Handled = true;
    }

    /// <summary>
    /// 在已绑定歌词栏里按下左键：选中标识线，或者开始拉选区框。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 事件的落点是 <c>LyricColumn</c> 那条 <see cref="Border"/>，而不是歌词标识层——
    /// 标识层盖在两栏之上却刻意不参与命中测试（参与了就会把波形的拖动、缩放、滚动整片挡掉）。
    /// 于是坐标要从这条 Border 换算到标识层的坐标系里，用的是
    /// <c>e.GetPosition(LyricMarks)</c>：两栏共用一个原点，这一次换算因此是零成本且精确的，
    /// 不必再减去波形栏宽度（那个宽度写错一次就会让点中的位置整体偏移）。
    /// </para>
    /// <para>
    /// <b>波形栏里的按下永远不会走到这里</b>，所以「拖动波形定位」与「拖动标识线」
    /// 这两件事天然不会打架，连判断谁优先都不需要。
    /// </para>
    /// </remarks>
    private void OnLyricColumnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LyricColumn).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(LyricMarks);

        // 箭头浮在歌词文字之上，那个位置同时也是某条标识线的标签区，所以必须先判它：
        // 若让 BeginGesture 先跑，这一下就变成「点中一条标识线」而把选区换掉，
        // 用户想移出三行却只选中了指针底下那一行。
        // 移出是一次立刻完成的操作，不需要捕获指针，也不需要锁住缩放。
        if (LyricMarks.TryUnbindAt(point))
        {
            e.Handled = true;
            return;
        }

        if (!LyricMarks.BeginGesture(point, e.KeyModifiers)) return;

        // 缩放是唯一能改变「两条标识线的屏幕间距」的东西，而拖动期间不重算合并与省略行数
        // 全靠那个间距不变。锁上之后 Ctrl+滚轮降级成普通滚动——手势还在进行中，
        // 让它在能做的那一半上继续起作用，比整片失效好。
        Waveform.ZoomLocked = true;

        _lyricPointer = e.Pointer;
        e.Pointer.Capture(LyricColumn);
        e.Handled = true;
    }

    private void OnLyricColumnMoved(object? sender, PointerEventArgs e)
    {
        if (!LyricMarks.IsGestureActive) return;

        LyricMarks.ContinueGesture(e.GetPosition(LyricMarks));
        e.Handled = true;
    }

    private void OnLyricColumnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!LyricMarks.IsGestureActive) return;

        LyricMarks.EndGesture();
        Waveform.ZoomLocked = false;

        _lyricPointer = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// 指针捕获被别处抢走（窗口失活、弹出对话框）时的收尾。
    /// </summary>
    /// <remarks>
    /// <b>被打断的手势一律放弃，不做半途的提交。</b>捕获丢失意味着用户并没有「松手」这个动作，
    /// 按屏幕上那一瞬间的样子落定就等于一次意外的时间轴改动；而没有撤销栈时，
    /// 意外改动比丢掉一次拖动更糟——重新拖一遍就是了。波形那边同样是放弃。
    /// </remarks>
    private void OnLyricColumnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!LyricMarks.IsGestureActive) return;

        CancelLyricGesture();
    }

    /// <summary>收掉进行中的选中 / 拖动，什么也不改。</summary>
    private void CancelLyricGesture()
    {
        LyricMarks.AbortGesture();
        Waveform.ZoomLocked = false;

        _lyricPointer?.Capture(null);
        _lyricPointer = null;
    }

    /// <summary>
    /// 把进行中的拖动落定并还回指针捕获。
    /// </summary>
    /// <remarks>
    /// 只给「用户在拖动途中主动要求做别的事」用（<c>Ctrl+S</c>）：他要保存的显然是屏幕上
    /// 正看着的那一份，而不是这次拖动开始之前的样子。被打断的手势则走
    /// <see cref="CancelLyricGesture"/>——区别在于中断是不是用户的意思。
    /// </remarks>
    private void CommitLyricGesture()
    {
        LyricMarks.EndGesture();
        Waveform.ZoomLocked = false;

        _lyricPointer?.Capture(null);
        _lyricPointer = null;
    }

    private void OnLyricSelectionChanged(object? sender, EventArgs e)
    {
        var count = LyricMarks.SelectionCount;

        if (count > 0)
        {
            _viewModel.StatusText = $"已选中 {count} 行歌词 —— 按住上下拖动可改时间码，"
                                  + "点右侧那个箭头可移出时间轴（回到「未绑定」栏），Esc 取消";
        }
    }

    /// <summary>
    /// 保存歌词（<c>Ctrl+S</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「第一次保存」与「之后每次」在这里也只差一步</b>：已经有主去处就直接写回，
    /// 没有就问一次保存位置。拿到位置之后 <see cref="MainWindowViewModel.SaveLyrics"/>
    /// 会把它记成主去处，于是「从零创建歌词」不需要任何额外的分支——粘贴进来一份歌词、
    /// 打完轴之后第一次按 <c>Ctrl+S</c>，走的正是这条路。
    /// </para>
    /// <para>
    /// 拖动还没松手就按下 <c>Ctrl+S</c> 时，先把这次拖动落定再保存：用户要保存的显然是
    /// 屏幕上正看着的那份，而不是它开始之前的样子。
    /// </para>
    /// </remarks>
    private async Task SaveLyricsAsync()
    {
        if (LyricMarks.IsGestureActive) CommitLyricGesture();

        if (!_viewModel.HasLyrics)
        {
            _viewModel.StatusText = "还没有歌词，没有可保存的内容";
            return;
        }

        var sink = _viewModel.SaveTarget;

        if (sink is null)
        {
            var suggested = _viewModel.SuggestedLyricPath;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存 LRC 歌词",
                SuggestedFileName = suggested is null ? "lyrics.lrc" : Path.GetFileName(suggested),
                DefaultExtension = "lrc",
                FileTypeChoices = [LyricFileType],
                ShowOverwritePrompt = true,
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            sink = new LrcFileSink(path);
        }

        _viewModel.SaveLyrics(sink);
    }

    private void OnPlayClick(object? sender, RoutedEventArgs e) => _viewModel.TogglePlayPause();

    /// <summary>
    /// 复位视图。
    /// </summary>
    /// <remarks>
    /// 直接调控件而不是走视图模型：适配所需的控件高度只有控件自己知道，
    /// 绕一圈把它塞进视图模型反而要多维护一个「视口高度」状态。
    /// </remarks>
    private void OnResetClick(object? sender, RoutedEventArgs e) => Waveform.FitToWindow();

    protected override void OnClosed(EventArgs e)
    {
        // 解码线程与音频设备都要显式收掉，否则关窗后进程可能还挂着。
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private async Task PickAndLoadAsync()
    {
        if (_viewModel.IsBusy) return;

        // 在弹对话框之前就挡下：换曲要连歌词一起清掉，而歌词里可能正躺着未保存的改动。
        // 让用户先挑一遍文件再告诉他「不行」是最没必要的一种打击。
        if (_viewModel.HasUnsavedLyrics)
        {
            _viewModel.StatusText = MainWindowViewModel.UnsavedLyricsHint;
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = false,
            FileTypeFilter = [AudioFileType],
        });

        if (files.Count == 0) return;

        // 非本地文件（网盘、压缩包内条目）拿不到本地路径，解码器只能处理本地文件。
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await _viewModel.LoadAsync(path);
    }

    /// <summary>
    /// 选一份 <c>.lrc</c> 并导入。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与打开音频不同，这条路径不检查 <see cref="MainWindowViewModel.IsBusy"/>：
    /// 解码可能正在进行，但歌词是一份独立的文本，任何时候都能读进来。
    /// </para>
    /// <para>
    /// 但必须要求先有音频。<c>.lrc</c> 是音频的侧车文件——没有音频，时间轴就没有基准，
    /// 标识线只能按一个默认缩放铺在「未载入音频」的提示上；而且载入音频时歌词会被清掉
    /// （换曲子后旧时间轴不再指向任何东西），先导入再开音频等于白做一次。
    /// 按钮平时就是灰的，这里再挡一道是为了 <c>Ctrl+I</c>。
    /// </para>
    /// <para>
    /// 也要求当前的歌词没有未保存的改动：导入一份新歌词会整份换掉手上这一份，
    /// 那与「换音频」是同一种丢失。同样在弹对话框之前就挡下。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 打开「导入歌词文本」对话框，把粘进来的歌词放进「未绑定」栏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与导入 <c>.lrc</c> 一样要求先有音频：这份歌词迟早要绑到这首曲子上，而打开音频
    /// 会清掉整份歌词（旧时间轴对新曲子毫无意义），先粘贴再打开等于白做一次。
    /// </para>
    /// <para>
    /// 但<b>不</b>检查有没有未保存的改动——这个操作是往文档里<b>追加</b>，不换掉任何东西，
    /// 而且它本身是一步可撤销的改动（<c>AddLyricLinesCommand</c>）。若拿「有未保存改动」
    /// 把它挡下来，用户就没法分两次粘歌词了，而那样做毫无风险。
    /// </para>
    /// </remarks>
    private async Task PickAndImportLyricTextAsync()
    {
        if (!_viewModel.HasAudio)
        {
            _viewModel.StatusText = "请先打开音频文件，再粘贴歌词 —— 这些歌词要绑到这首曲子的时间轴上";
            return;
        }

        var text = await new ImportLyricsDialog().ShowDialog<string?>(this);

        if (string.IsNullOrEmpty(text)) return;

        _viewModel.ImportLyricText(text);
    }

    private async Task PickAndImportLyricsAsync()
    {
        if (!_viewModel.HasAudio)
        {
            _viewModel.StatusText = "请先打开音频文件，再导入歌词 —— .lrc 是音频的侧车文件";
            return;
        }

        if (_viewModel.HasUnsavedLyrics)
        {
            _viewModel.StatusText = MainWindowViewModel.UnsavedLyricsHint;
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 LRC 歌词",
            AllowMultiple = false,
            FileTypeFilter = [LyricFileType],
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _viewModel.LoadLyrics(path);
    }
}
