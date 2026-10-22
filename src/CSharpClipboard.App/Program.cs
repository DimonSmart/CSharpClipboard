using CSharpClipboard.Platform.MacOS;
using Uno.UI.Hosting;

namespace CSharpClipboard.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine(typeof(App).Assembly.GetName().Version);
            return 0;
        }

        if (args.Contains("--bridge-check", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
        {
            MacOSIntegration.VerifyBridge();
            return 0;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("This build requires Windows or macOS.");

        // Acquire ownership before the Uno event loop, clipboard monitoring or SQLite writes.
        // Duplicate launches only signal the original process and never initialize capture.
        var directory = OperatingSystem.IsMacOS()
            ? MacOSIntegration.GetDataDirectory()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSharpClipboard");
        using var lease = SingleInstanceLease.AcquireAsync(directory).GetAwaiter().GetResult();
        if (lease is null) return 0;

        App? application = null;
        var backend = args.Contains("--software-rendering", StringComparer.Ordinal)
            ? Win32RenderingBackend.Software
            : Win32RenderingBackend.Vulkan;
        var host = UnoPlatformHostBuilder.Create()
            .App(() => application = new App(lease))
            .UseWin32(builder => builder.RenderingBackend(backend))
            .UseMacOS()
            .Build();

        try { host.RunAsync().GetAwaiter().GetResult(); return application?.ExitCode ?? 0; }
        finally { application?.StopAfterHost(); }
    }
}
