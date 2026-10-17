using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.App.Views;

/// <summary>
/// 「导入歌词文本」的对话框：把一段歌词粘进来，确认后交给主窗口。
/// </summary>
/// <remarks>
/// <para>
/// <b>它只负责拿到文本，不认识歌词。</b>切行由 <see cref="PlainTextLyrics"/> 做，
/// 建行、进历史、落进「未绑定」栏由视图模型做。于是这个窗口里没有一行代码需要知道
/// 歌词文档长什么样——将来「从剪贴板直接导入」只需要绕过这个窗口、把同一份文本交给同一个方法。
/// </para>
/// <para>
/// 行数在底下实时显示：粘贴一大段之后，用户唯一能核对「是不是都进来了」的手段就是它。
/// </para>
/// </remarks>
public partial class ImportLyricsDialog : Window
{
    public ImportLyricsDialog()
    {
        InitializeComponent();
        UpdateCount();
    }

    /// <summary>把编辑框里的文本重新数一遍，并决定「导入」能不能按。</summary>
    private void UpdateCount()
    {
        var count = PlainTextLyrics.Split(Input.Text).Count;

        CountText.Text = count == 0 ? "还没有内容" : $"{count} 行歌词";
        ImportButton.IsEnabled = count > 0;
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e) => UpdateCount();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnImportClick(object? sender, RoutedEventArgs e) => Close(Input.Text);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 打开就把光标放进编辑框：用户点这个按钮的目的就是要粘贴，让他直接按 Ctrl+V。
        Input.Focus();
    }

    /// <summary>
    /// <c>Esc</c> 关掉，返回 <c>null</c>（与「取消」同一条路）。
    /// </summary>
    /// <remarks>
    /// 只认 <c>Esc</c>，不认回车——这个窗口里回车是换行。理由见 XAML 里那段注释。
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
            return;
        }

        base.OnKeyDown(e);
    }
}
