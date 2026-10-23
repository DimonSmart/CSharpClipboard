using CSharpClipboard.Platform.Windows;

namespace CSharpClipboard.Platform.Windows.Tests;

public sealed class WindowsConsoleTargetPolicyTests
{
    [Theory]
    [InlineData("ConsoleWindowClass", "conhost")]
    [InlineData("ConsoleWindowClass", "OpenConsole")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminalPreview")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminalCanary")]
    [InlineData("consolewindowclass", "CONHOST")]
    public void Only_known_class_and_process_pairs_allow_console_fallback(string windowClass, string processName)
    {
        Assert.True(WindowsConsoleTargetPolicy.IsRecognizedConsoleHost(windowClass, processName));
    }

    [Theory]
    [InlineData("ConsoleWindowClass", "notepad")]
    [InlineData("ConsoleWindowClass", "cmd")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "conhost")]
    [InlineData("Chrome_WidgetWin_1", "WindowsTerminal")]
    [InlineData("Chrome_WidgetWin_1", "Code")]
    [InlineData("Edit", "conhost")]
    [InlineData("ConsoleWindowClass", "FakeTerminal")]
    [InlineData("", "WindowsTerminal")]
    public void Unrecognized_windows_never_get_console_fallback(string windowClass, string processName)
    {
        Assert.False(WindowsConsoleTargetPolicy.IsRecognizedConsoleHost(windowClass, processName));
    }
}
