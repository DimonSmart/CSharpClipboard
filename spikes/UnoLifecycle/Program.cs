using Uno.UI.Hosting;

namespace CSharpClipboard.UnoLifecycleProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ProbeLog.Write("Bootstrap starting; Windows backend = " +
            (args.Contains("--software", StringComparer.Ordinal) ? "Software" : "Vulkan"));

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            ProbeLog.Write("Unsupported OS for this lifecycle probe.");
            return 2;
        }

        try
        {
            var backend = args.Contains("--software", StringComparer.Ordinal)
                ? Win32RenderingBackend.Software
                : Win32RenderingBackend.Vulkan;

            var host = UnoPlatformHostBuilder.Create()
                .App(() => new App(args))
                .UseWin32(builder => builder.RenderingBackend(backend))
                .UseMacOS()
                .Build();

            ProbeLog.Write("Uno host built; entering RunAsync.");
            host.RunAsync().GetAwaiter().GetResult();
            ProbeLog.Write("Uno RunAsync returned.");
            return 0;
        }
        catch (Exception exception)
        {
            ProbeLog.Write("FAILED: " + exception);
            return 1;
        }
    }
}
