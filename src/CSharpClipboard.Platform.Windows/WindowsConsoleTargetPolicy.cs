namespace CSharpClipboard.Platform.Windows;

// Class AND process identity are required: a generic "console-like" HWND is not
// sufficient to permit a paste when UI Automation cannot identify its control.
internal static class WindowsConsoleTargetPolicy
{
    internal static bool IsRecognizedConsoleHost(string windowClass, string processName)
    {
        if (windowClass.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
            return processName.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase);

        if (windowClass.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase))
            return processName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("WindowsTerminalPreview", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("WindowsTerminalCanary", StringComparison.OrdinalIgnoreCase);

        return false;
    }
}

internal enum WindowsFocusIdentityState { Verified, Unavailable, Protected }
