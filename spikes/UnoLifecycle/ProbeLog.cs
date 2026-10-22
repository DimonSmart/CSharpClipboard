using System.Text;

namespace CSharpClipboard.UnoLifecycleProbe;

internal static class ProbeLog
{
    private static readonly object Gate = new();

    private static readonly string FileName = Path.Combine(
        Path.GetTempPath(),
        $"CSharpClipboard-UnoLifecycle-{Environment.ProcessId}.log");

    internal static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            File.AppendAllText(FileName, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
