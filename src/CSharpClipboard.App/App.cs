using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Controls;
using CSharpClipboard.Platform.Windows;
using CSharpClipboard.Platform.MacOS;
using Avalonia.Threading;

namespace CSharpClipboard.App;

public sealed class App : Application
{
    private IDesktopShell? _shell;

    public override void Initialize()
    {
        Name = "CSharpClipboard";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += OnShutdownRequested;
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var lease = await SingleInstanceLease.AcquireAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSharpClipboard"));
                if (lease is null) { desktop.Shutdown(); return; }
                WindowsClipboardPort? clipboard = null;
                try { clipboard = new WindowsClipboardPort(); _shell = new DesktopShell<WindowsPasteSnapshot>(clipboard, clipboard, new WindowsPasteTargetPort(clipboard), new WindowsDesktopServices(clipboard), desktop, lease); }
                catch { clipboard?.Dispose(); lease.Dispose(); throw; }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var lease = await SingleInstanceLease.AcquireAsync(MacOSIntegration.GetDataDirectory());
                if (lease is null) { desktop.Shutdown(); return; }
                try
                {
                    var integration = new MacOSIntegration(async action => await Dispatcher.UIThread.InvokeAsync(action));
                    _shell = new DesktopShell<MacOSPasteSnapshot>(integration, integration, integration, integration, desktop, lease);
                }
                catch { lease.Dispose(); throw; }
            }
            else throw new PlatformNotSupportedException("This build requires Windows or macOS.");
            await _shell.StartAsync();
        }
        catch (Exception error)
        {
            var explanation = error switch
            {
                CSharpClipboard.Storage.HistoryDatabaseVersionException => "History was created by a newer application version. Install a matching version; the database has not been downgraded.",
                System.Text.Json.JsonException or InvalidDataException => "Settings could not be loaded. The existing file has been preserved in the application data directory.",
                DllNotFoundException or EntryPointNotFoundException => "The macOS bridge is missing or incompatible. Reinstall the complete matching .app bundle.",
                _ => $"CSharpClipboard could not start ({error.GetType().Name}). Existing history and settings have been preserved."
            };
            var message = new TextBlock { Text = explanation, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(20) };
            var window = new Window { Title = "CSharpClipboard — Startup unavailable", Width = 480, Height = 180, Content = message };
            window.Closed += (_, _) => desktop.TryShutdown(1); window.Show();
        }
    }
    private bool _shuttingDown;
    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (_shuttingDown) return;
        args.Cancel = true;
        _shuttingDown = true;
        var exitCode = 0;
        try { if (_shell is not null) await _shell.DisposeAsync(); }
        catch { exitCode = 1; }
        finally { ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown(exitCode); }
    }
}
