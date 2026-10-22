using CSharpClipboard.Platform.MacOS;
using CSharpClipboard.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CSharpClipboard.App;

public sealed partial class App : Application
{
    private readonly SingleInstanceLease _lease;
    private IDesktopShell? _shell;
    private int _stopping;
    private Task? _stopTask;
    internal int ExitCode { get; private set; }

    internal App(SingleInstanceLease lease)
    {
        _lease = lease;
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Uno UI dispatcher is unavailable.");
            if (OperatingSystem.IsWindows())
            {
                var clipboard = new WindowsClipboardPort();
                try
                {
                    var platform = new WindowsDesktopServices(clipboard);
                    _shell = new DesktopShell<WindowsPasteSnapshot>(
                        clipboard, clipboard, new WindowsPasteTargetPort(clipboard), platform,
                        dispatcher, RequestQuit, _lease);
                }
                catch { clipboard.Dispose(); throw; }
            }
            else
            {
                var integration = new MacOSIntegration(action =>
                {
                    if (dispatcher.HasThreadAccess) { action(); return Task.CompletedTask; }
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!dispatcher.TryEnqueue(() =>
                    {
                        try { action(); completion.SetResult(); }
                        catch (Exception exception) { completion.SetException(exception); }
                    }))
                        completion.SetException(new InvalidOperationException("Uno UI dispatcher stopped."));
                    return completion.Task;
                });
                _shell = new DesktopShell<MacOSPasteSnapshot>(
                    integration, integration, integration, integration, dispatcher, RequestQuit, _lease);
            }

            await _shell.StartAsync();
        }
        catch (Exception exception)
        {
            ExitCode = 1;
            System.Diagnostics.Trace.TraceError("CSharpClipboard startup failed: " + exception);
            var window = new Window
            {
                Title = "CSharpClipboard — Startup unavailable",
                Content = new StackPanel
                {
                    Padding = new Thickness(20),
                    Children =
                    {
                        new TextBlock { Text = StartupExplanation(exception), TextWrapping = TextWrapping.Wrap },
                        new Button { Content = "Quit", Command = null }
                    }
                }
            };
            if (window.Content is StackPanel stack && stack.Children.LastOrDefault() is Button quit)
                quit.Click += (_, _) => RequestQuit();
            window.AppWindow.Closing += (_, _) => RequestQuit();
            window.Activate();
        }
    }

    private static string StartupExplanation(Exception error) => error switch
    {
        CSharpClipboard.Storage.HistoryDatabaseCorruptException =>
            "History database is damaged. Quit the application, then restore a valid backup or remove history.sqlite and its -wal/-shm files from the application data folder to start with an empty history. Settings were preserved.",
        CSharpClipboard.Storage.HistoryDatabaseVersionException =>
            "History was created by a newer application version. Existing data was preserved.",
        System.Text.Json.JsonException or InvalidDataException =>
            "Settings could not be loaded. The original settings file was preserved.",
        DllNotFoundException or EntryPointNotFoundException =>
            "The macOS native bridge is missing or incompatible. Reinstall the complete .app bundle.",
        _ => $"CSharpClipboard could not start ({error.GetType().Name}). Existing history and settings were preserved."
    };

    private void RequestQuit() => _ = StopAsync();

    private Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return _stopTask ?? Task.CompletedTask;
        return _stopTask = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try { if (_shell is not null) await _shell.DisposeAsync(); }
        catch (Exception exception)
        {
            ExitCode = 1;
            System.Diagnostics.Trace.TraceError("CSharpClipboard shutdown failed: " + exception);
        }
        finally { Exit(); }
    }

    // The managed cleanup normally runs BEFORE the native loop exits.
    internal void StopAfterHost()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0 && _shell is not null)
            _shell.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
