using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace CSharpClipboard.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine(typeof(App).Assembly.GetName().Version); return;
        }
        if (args.Contains("--bridge-check", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
        {
            CSharpClipboard.Platform.MacOS.MacOSIntegration.VerifyBridge(); return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect();
}
