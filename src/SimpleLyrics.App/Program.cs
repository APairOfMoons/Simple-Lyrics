using Avalonia;

namespace SimpleLyrics.App;

internal static class Program
{
    // Avalonia 需要 STA 线程；不要改成 MTA。
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // 供 Avalonia 预览器与单元测试复用，签名不要改。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
