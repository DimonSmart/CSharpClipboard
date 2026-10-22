namespace CSharpClipboard.Platform.Windows;

public static class WindowsAdapterComposition
{
    public static WindowsClipboardPort CreateClipboardPort() => new();
}
