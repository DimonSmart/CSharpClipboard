using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CSharpClipboard.UnoLifecycleProbe;

// Deliberately starts with zero windows. It is NOT the production clipboard application.
public sealed partial class App : Application
{
    private readonly string[] _options;
    private DispatcherQueue? _dispatcher;
    private Window? _window;

    internal App(string[] options)
    {
        _options = options;
        InitializeComponent();
        ProbeLog.Write("App constructed; no window created.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ProbeLog.Write("OnLaunched; dispatcher available = " + (_dispatcher is not null));

        if (_dispatcher is null)
        {
            // This is a blocking spike failure. Exit should still stop the native loop.
            Exit();
            return;
        }

        if (Has("--start-visible"))
        {
            OpenWindow();
            return;
        }

        if (Has("--no-window"))
        {
            Schedule(5000, "Quit without any window ever being created", Quit);
            return;
        }

        // Default automated sequence is intentionally NOT a headless host:
        // it tests the actual Win32/AppKit host and the first-window boundary.
        Schedule(2500, "Open first window from zero-window state", OpenWindow);
        Schedule(4500, "Hide the only window", HideWindow);
        Schedule(6500, "Reactivate the same window", OpenWindow);
        Schedule(8500, "Close the last window", CloseWindow);
        Schedule(10500, "Quit after last-window close", Quit);
    }

    private bool Has(string option) => _options.Contains(option, StringComparer.Ordinal);

    private void Schedule(int milliseconds, string label, Action action)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(milliseconds).ConfigureAwait(false);
            var queued = _dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    ProbeLog.Write("DISPATCH: " + label);
                    action();
                }
                catch (Exception exception)
                {
                    ProbeLog.Write("ACTION FAILED (" + label + "): " + exception);
                    Quit();
                }
            }) == true;
            ProbeLog.Write($"Scheduled '{label}' to UI dispatcher = {queued}");
        });
    }

    private void OpenWindow()
    {
        if (_window is null)
        {
            var window = new Window { Title = "CSharpClipboard Uno lifecycle probe" };
            var layout = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
            layout.Children.Add(new TextBlock
            {
                Text = "Lifecycle probe only. No clipboard monitoring, tray, SQLite or login registration."
            });

            var hide = new Button { Content = "Hide window" };
            hide.Click += (_, _) => HideWindow();
            layout.Children.Add(hide);

            var close = new Button { Content = "Close window" };
            close.Click += (_, _) => CloseWindow();
            layout.Children.Add(close);

            var quit = new Button { Content = "Quit app" };
            quit.Click += (_, _) => Quit();
            layout.Children.Add(quit);

            window.Content = layout;
            window.Closed += (_, _) =>
            {
                ProbeLog.Write("Window.Closed fired.");
                if (ReferenceEquals(_window, window))
                    _window = null;
            };
            window.AppWindow.Closing += (_, _) => ProbeLog.Write("AppWindow.Closing fired.");
            _window = window;
            ProbeLog.Write("Created first Uno Window instance.");
        }

        _window.Activate();
        ProbeLog.Write("Window.Activate called.");
    }

    private void HideWindow()
    {
        _window?.AppWindow.Hide();
        ProbeLog.Write("AppWindow.Hide called; host must remain alive.");
    }

    private void CloseWindow()
    {
        _window?.Close();
        ProbeLog.Write("Window.Close called; host may terminate here (record actual result).");
    }

    private void Quit()
    {
        ProbeLog.Write("Explicit Application.Exit requested.");
        Exit();
    }
}
