using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimpleLyrics.App.ViewModels;

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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // 用隧道阶段接住快捷键：冒泡阶段可能已被获得焦点的子控件消费掉，
        // 空格尤其如此——按钮拿到焦点后会把空格当成「点击」。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        // 失活时那个「松开」的事件不一定送得到，状态位会永久卡住，
        // 之后所有空格都会被当成自动重复而全部吞掉。
        Deactivated += (_, _) => _spaceHeld = false;
    }

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
}
