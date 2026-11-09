using System.Text.RegularExpressions;
using CSharpClipboard.Core;
using CSharpClipboard.Services;
using CSharpClipboard.Storage;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace CSharpClipboard.App;

internal interface IDesktopShell : IAsyncDisposable
{
    Task StartAsync();
}

// Application state (capture, storage, safe paste, tray) is independent of all Uno windows.
internal sealed class DesktopShell<TSnapshot> : IDesktopShell, IHistoryPanelPort
    where TSnapshot : class, IPasteTargetSnapshot
{
    private readonly IClipboardPort _clipboard;
    private readonly IClipboardWritePort _writer;
    private readonly IPasteTargetPort<TSnapshot> _targets;
    private readonly IDesktopPlatformServices _platform;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _quit;
    private readonly HistoryPresentation _presentation = new();
    private readonly SemaphoreSlim _toggle = new(1, 1);
    private readonly WindowVisibilityTracker<Window> _visible = new(
        activate: window => window.Activate(),
        showAfterHide: window =>
        {
            if (OperatingSystem.IsWindows()) NativeWindowVisibility.Show(window);
        },
        hide: NativeWindowVisibility.Hide);
    private readonly List<Window> _windows = [];
    private readonly HashSet<Guid> _pinsInProgress = [];
    private readonly SingleInstanceLease _instance;
    private IHistoryRepository? _history;
    private IHistoryMaintenance? _maintenance;
    private ClipboardCaptureCoordinator? _capture;
    private PasteService<TSnapshot>? _paste;
    private HistoryInvocation<TSnapshot>? _invocation;
    private JsonSettingsStore? _settings;
    private AppPreferences _preferences = new();
    private Window? _panel, _settingsWindow, _about;
    private HistoryView? _view;
    private int _searchVersion;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _closing;
    private bool _initialHistoryFocusPending;
    private string _lastHistorySearchText = "";
    private int _disposed;

    internal Window? HistoryWindow => _panel;
    internal Window? SettingsWindow => _settingsWindow;
    internal Window? AboutWindow => _about;

    public DesktopShell(IClipboardPort clipboard, IClipboardWritePort writer, IPasteTargetPort<TSnapshot> targets,
        IDesktopPlatformServices platform, DispatcherQueue dispatcher, Action quit, SingleInstanceLease instance)
    {
        _clipboard = clipboard;
        _writer = writer;
        _targets = targets;
        _platform = platform;
        _dispatcher = dispatcher;
        _quit = quit;
        _instance = instance;
    }

    public async Task StartAsync()
    {
        _settings = new JsonSettingsStore(Path.Combine(_platform.DataDirectory, "settings.json"));
        _preferences = await _settings.GetAsync<AppPreferences>("preferences", default) ?? new AppPreferences();
        if (_preferences.Version != 1)
            throw new InvalidDataException("Settings version is newer than the application.");

        var storage = await Task.Run(async () => await SqliteHistoryRepository.OpenAsync(DatabasePath,
            new HistoryRepositoryOptions()));
        _history = storage;
        _maintenance = storage;
        _capture = new ClipboardCaptureCoordinator(_clipboard, _history);
        _capture.StatusChanged += message => Post(() => { if (_view is not null) _view.StatusLabel.Text = message; });
        if (_preferences.CapturePaused) await _capture.PauseAsync(); else await _capture.StartAsync();
        _paste = new PasteService<TSnapshot>(_targets, this, _clipboard, _writer, _capture);

        _instance.Activated += RequestHistory;
        _platform.HistoryRequested += RequestHistory;
        if (_platform is not IStatusItemPlatform status)
            throw new InvalidOperationException("The OS platform must supply a native tray/status item.");
        await status.CreateStatusItemAsync(OnStatusItemAction, _preferences.CapturePaused, default);
        if (_preferences.Shortcut is { } shortcut)
            await RunAsync(async () => await _platform.RegisterAsync(shortcut, default));

        if (!_preferences.Onboarded)
        {
            ShowSettings();
            _preferences = _preferences with { Onboarded = true };
            await SaveAsync();
        }
    }

    private string DatabasePath => Path.Combine(_platform.DataDirectory, "history.sqlite");
    private string Modifier => _platform.UsesCommandKey ? "Cmd" : "Ctrl";

    private void Post(Action action)
    {
        if (!_closing) _dispatcher.TryEnqueue(() => { if (!_closing) action(); });
    }

    private void RequestHistory() => Post(() => _ = RunAsync(ToggleHistoryAsync));
    private void OnStatusItemAction(StatusItemAction action) => Post(() => _ = RunAsync(() => HandleActionAsync(action)));
    private async Task HandleActionAsync(StatusItemAction action)
    {
        switch (action)
        {
            case StatusItemAction.OpenHistory: await ToggleHistoryAsync(); break;
            case StatusItemAction.ToggleCapture: await TogglePauseAsync(); break;
            case StatusItemAction.Settings: ShowSettings(); break;
            case StatusItemAction.ClearRecentHistory: await ClearRecentAsync(); break;
            case StatusItemAction.About: ShowAbout(); break;
            case StatusItemAction.Quit: _quit(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    internal async Task ToggleHistoryAsync()
    {
        if (!await _toggle.WaitAsync(0)) return;
        try
        {
            if (IsShown(_panel))
            {
                if (_platform is IStatusItemPlatform status)
                    await status.DiscardPendingStatusItemSnapshotAsync(default);
                await CloseHistoryAsync();
                return;
            }
            if (_invocation is not null)
            {
                await _invocation.DisposeAsync();
                _invocation = null;
            }
            _invocation = await _paste!.OpenHistoryAsync();
            await UpdateCaptureStatusAsync();
        }
        finally { _toggle.Release(); }
    }

    public async ValueTask ShowAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var entries = await Task.Run(async () => await _history!.GetEntriesAsync(null, cancellationToken), cancellationToken);
        await UiAsync(() =>
        {
            EnsurePanel();
            _presentation.Open(entries);
            ++_searchVersion; // Invalidate unfinished filtering from the previous opening.
            _lastHistorySearchText = "";
            _view!.SearchBox.Text = "";
            RenderRows();
            _initialHistoryFocusPending = true;
            ShowWindow(_panel!);
            // Uno can activate and measure the native window after Show returns.
            Post(FocusInitialHistorySelection);
        });
    }

    public ValueTask HideAsync(Guid invocationId, CancellationToken cancellationToken) =>
        new(UiAsync(() => { _initialHistoryFocusPending = false; if (_panel is not null) HideWindow(_panel); }));

    internal async Task CloseHistoryAsync()
    {
        _initialHistoryFocusPending = false;
        ++_searchVersion;
        if (_panel is not null) HideWindow(_panel);
        if (_invocation is { } current)
        {
            _invocation = null;
            await current.DisposeAsync();
        }
    }

    private void EnsurePanel()
    {
        if (_panel is not null) return;
        _view = new HistoryView();
        _panel = NewWindow("Clipboard History", 640, 500, _view,
            () => { _ = RunAsync(CloseHistoryAsync); });
        _view.KeyboardHints.Text = $"Enter Paste · {Modifier}+Enter Copy · {Modifier}+P Pin · Escape Close";
        _view.ClearButton.Click += async (_, _) => await RunAsync(ClearRecentAsync);
        _view.SettingsButton.Click += (_, _) => ShowSettings();
        _view.HistoryList.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ListViewItem item && item.Tag is HistoryEntry entry)
                _ = RunAsync(() => SelectAsync(entry, false));
        };
        // PreviewKeyDown precedes ListViewItem's built-in arrow/Enter handling.
        _view.HistoryList.PreviewKeyDown += HistoryListPreviewKeyDown;
        _view.SearchBox.KeyDown += PanelKeyDown;
        _view.SearchBox.TextChanged += async (_, _) =>
        {
            // The programmatic reset at opening must not recreate a focused row.
            var query = _view.SearchBox.Text;
            if (query == _lastHistorySearchText) return;
            _lastHistorySearchText = query;
            if (!IsShown(_panel)) return;
            var version = ++_searchVersion;
            await Task.Delay(100);
            if (version == _searchVersion && IsShown(_panel)) await RunAsync(UpdateRowsAsync);
        };
        _view.Loaded += (_, _) => Post(FocusInitialHistorySelection);
        _panel.Activated += (_, args) =>
        {
            if (args.WindowActivationState != CoreWindowActivationState.Deactivated)
            {
                // One-shot focus: never steal focus again from search or row buttons.
                Post(FocusInitialHistorySelection);
                return;
            }
            // Deactivation may be caused by an intentional context menu or preview.
            if (!_closing && IsShown(_panel))
                _ = RunAsync(CloseHistoryAsync);
        };
    }

    private Window NewWindow(string title, int width, int height, UIElement content, Action? onHide = null)
    {
        var window = new Window { Title = title, Content = content };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height });
        window.AppWindow.Closing += (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true;
            // Defer the hide until the native close callback has completed.
            if (!_dispatcher.TryEnqueue(() =>
            {
                if (_closing) return;
                try
                {
                    if (onHide is not null) onHide();
                    else HideWindow(window);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("CSharpClipboard window hide failed: " + error);
                }
            }))
            {
                System.Diagnostics.Trace.TraceError("CSharpClipboard could not queue a window hide after close.");
            }
        };
        _windows.Add(window);
        return window;
    }

    private void ShowWindow(Window window) => _visible.Show(window);
    private void HideWindow(Window window) => _visible.Hide(window);
    private bool IsShown(Window? window) => _visible.IsVisible(window);

    private void FocusInitialHistorySelection()
    {
        if (!_initialHistoryFocusPending || !IsShown(_panel) || _view is null) return;
        var list = _view.HistoryList;
        if (list.Items.Count == 0)
        {
            if (_view.SearchBox.Focus(FocusState.Programmatic))
                _initialHistoryFocusPending = false;
            return;
        }
        list.SelectedIndex = 0;
        var first = (ListViewItem)list.Items[0];
        list.ScrollIntoView(first);
        if (first.Focus(FocusState.Keyboard) || list.Focus(FocusState.Keyboard))
            _initialHistoryFocusPending = false;
    }

    private void RenderRows() => ApplyRows(_presentation.Filter(_view?.SearchBox.Text ?? ""));
    private async Task UpdateRowsAsync()
    {
        var version = ++_searchVersion;
        var query = _view?.SearchBox.Text ?? "";
        var rows = await Task.Run(() => _presentation.Filter(query));
        if (version == _searchVersion && !_closing) ApplyRows(rows);
    }

    private void ApplyRows(IReadOnlyList<HistoryEntry> rows)
    {
        if (_view is null) return;
        var list = _view.HistoryList;
        var selected = (list.SelectedItem as ListViewItem)?.Tag is HistoryEntry old ? old.Id : Guid.Empty;
        list.Items.Clear();
        foreach (var entry in rows)
        {
            var item = new ListViewItem { Tag = entry, Padding = new Thickness(8, 4, 8, 4) };
            var layout = new Grid { ColumnSpacing = 8 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var preview = Regex.Replace(entry.Text.Length > 700 ? entry.Text[..700] : entry.Text, @"\s+", " ");
            layout.Children.Add(new TextBlock
            {
                Text = preview,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 64,
                VerticalAlignment = VerticalAlignment.Center
            });
            var pinLabel = entry.PinnedAt is null ? "Pin" : "Unpin";
            var pin = new Button
            {
                Content = CreatePinIcon(entry.PinnedAt is not null),
                Width = 32, Height = 32, Padding = new Thickness(5)
            };
            pin.Click += (_, _) => _ = RunAsync(() => PinAsync(entry));
            AutomationProperties.SetName(pin, pinLabel);
            ToolTipService.SetToolTip(pin, pinLabel);
            Grid.SetColumn(pin, 1);
            layout.Children.Add(pin);
            item.Content = layout;
            list.Items.Add(item);
            if (entry.Id == selected) list.SelectedItem = item;
        }
        if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0;
        _view.EmptyMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.EmptyMessage.Text = string.IsNullOrEmpty(_view.SearchBox.Text) ? "Copy text to start your history" : "No matches";
    }

    // The pin is vector geometry, so it does not depend on macOS system fonts.
    private static PathIcon CreatePinIcon(bool pinned)
    {
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(7, 3), IsClosed = true };
        foreach (var (x, y) in new (double X, double Y)[]
        {
            (17, 3), (16, 6), (16, 12), (19, 15), (19, 17), (13, 17),
            (12, 23), (11, 17), (5, 17), (5, 15), (8, 12), (8, 6)
        })
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, y) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new PathIcon { Data = geometry, Width = 20, Height = 20, Opacity = pinned ? 1 : 0.45 };
    }

    private async void HistoryListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_view is null) return;
        // Let the Pin button keep its native Enter/arrow behavior after Tab.
        var focusedButton = _view.XamlRoot is not null &&
            FocusManager.GetFocusedElement(_view.XamlRoot) is Button;
        if (focusedButton && e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Enter) return;
        if (e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            var list = _view.HistoryList;
            var index = HistoryListNavigation.NextIndex(list.Items.Count, list.SelectedIndex,
                e.Key == VirtualKey.Down ? 1 : -1);
            if (index < 0) return;
            e.Handled = true;
            list.SelectedIndex = index;
            var item = (ListViewItem)list.Items[index];
            list.ScrollIntoView(item);
            if (!item.Focus(FocusState.Keyboard)) list.Focus(FocusState.Keyboard);
            return;
        }
        await HandlePanelKeyAsync(e);
    }

    private async void PanelKeyDown(object sender, KeyRoutedEventArgs e) =>
        await HandlePanelKeyAsync(e);

    private async Task HandlePanelKeyAsync(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            await RunAsync(CloseHistoryAsync);
            return;
        }
        if ((_view?.HistoryList.SelectedItem as ListViewItem)?.Tag is not HistoryEntry entry) return;
        var command = Down(_platform.UsesCommandKey ? VirtualKey.LeftWindows : VirtualKey.Control) ||
                      (_platform.UsesCommandKey && Down(VirtualKey.RightWindows));
        Func<Task>? action = e.Key switch
        {
            VirtualKey.Enter => () => SelectAsync(entry, command),
            VirtualKey.P when command => () => PinAsync(entry),
            _ => null
        };
        if (action is not null) { e.Handled = true; await RunAsync(action); }
    }

    private static bool Down(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);

    private async Task SelectAsync(HistoryEntry entry, bool copy)
    {
        if (_invocation is not { } invocation) return;
        var outcome = await _paste!.SelectAsync(invocation, entry.Text,
            copy ? HistorySelectionAction.CopyOnly : HistorySelectionAction.Paste);
        if (_view is null) return;
        _view.StatusLabel.Text = outcome switch
        {
            HistorySelectionOutcome.CopiedOnly => $"Copied. Press {Modifier}+V to paste.",
            HistorySelectionOutcome.ShortcutSent => "Paste shortcut sent.",
            HistorySelectionOutcome.ClipboardChanged => "Clipboard changed. Paste was cancelled.",
            HistorySelectionOutcome.RejectedDuplicate => _view.StatusLabel.Text,
            _ => "Could not complete the action. History is preserved."
        };
        if (outcome != HistorySelectionOutcome.RejectedDuplicate)
            ShowActionFeedback(_view.StatusLabel.Text);
    }

    private async void ShowActionFeedback(string message)
    {
        if (_view is null) return;
        var view = _view;
        view.FeedbackLabel.Text = message;
        view.FeedbackLabel.Visibility = Visibility.Visible;
        await Task.Delay(3000);
        Post(() =>
        {
            if (!ReferenceEquals(_view, view)) return;
            view.FeedbackLabel.Text = "";
            view.FeedbackLabel.Visibility = Visibility.Collapsed;
        });
    }

    private async Task PinAsync(HistoryEntry entry)
    {
        // A second click/key press may arrive before the first update has rendered.
        if (!_pinsInProgress.Add(entry.Id)) return;
        try
        {
            await Task.Run(async () =>
            {
                if (entry.PinnedAt is null) await _history!.PinAsync(entry.Id, default);
                else await _history!.UnpinAsync(entry.Id, default);
            });
            await RefreshAsync();
        }
        finally { _pinsInProgress.Remove(entry.Id); }
    }
    private async Task RefreshAsync()
    {
        if (_history is null || _closing) return;
        if (_refreshing) { _refreshPending = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                if (IsShown(_panel))
                {
                    var entries = await Task.Run(async () => await _history.GetEntriesAsync(null, default));
                    _presentation.Refresh(entries);
                    await UpdateRowsAsync();
                }
            }
            while (_refreshPending && !_closing);
        }
        finally { _refreshing = false; }
    }

    private async Task UpdateCaptureStatusAsync()
    {
        var access = await _platform.GetClipboardAccessAsync(default);
        if (_view is { } view && string.IsNullOrEmpty(view.StatusLabel.Text))
            view.StatusLabel.Text = _preferences.CapturePaused ? "Capture paused"
            : access == PermissionStatus.Allowed ? "Capture active · Copy Only available · Paste checks original field"
            : "Clipboard Access Required · Copy Only available";
    }

    private async Task TogglePauseAsync()
    {
        var pause = !_preferences.CapturePaused;
        if (pause) await _capture!.PauseAsync(); else await _capture!.ResumeAsync();
        _preferences = _preferences with { CapturePaused = pause };
        await SaveAsync();
        if (_platform is IStatusItemPlatform status)
            await status.SetStatusItemCapturePausedAsync(pause, default);
        if (_view is not null) _view.StatusLabel.Text = pause ? "Capture paused" : "Capture active";
    }

    private Task SaveAsync() => _settings!.SetAsync("preferences", _preferences, default).AsTask();
    private async Task ClearRecentAsync()
    {
        await Task.Run(async () => await _history!.ClearRecentAsync(default));
        await Task.Run(() => _maintenance!.MaintainAsync());
        await RefreshAsync();
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            if (_view is not null) _view.StatusLabel.Text =
                $"Operation unavailable ({error.GetType().Name}). History is preserved.";
            System.Diagnostics.Trace.TraceError("CSharpClipboard operation failed: " + error.GetType().Name);
        }
    }

    private Task UiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess) { action(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception error) { tcs.SetException(error); }
        }))
            tcs.SetException(new InvalidOperationException("Uno dispatcher unavailable."));
        return tcs.Task;
    }

    private static Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await action();
        return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private string FormatShortcut(GlobalShortcut shortcut)
    {
        var parts = new List<string>();
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Meta)) parts.Add(_platform.UsesCommandKey ? "Cmd" : "Win");
        parts.Add(shortcut.Key.ToUpperInvariant());
        return string.Join("+", parts);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            ShowWindow(_settingsWindow);
            return;
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
        var scroll = new ScrollViewer();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var window = NewWindow("CSharpClipboard Settings", 500, 360, root);
        _settingsWindow = window;

        StackPanel NewPage() => new() { Padding = new Thickness(16), Spacing = 10 };
        var general = NewPage();
        var login = new CheckBox { Content = "Launch at Login" };
        general.Children.Add(login);
        _ = RunAsync(async () =>
        {
            login.IsChecked = await _platform.IsEnabledAsync(default);
            ToolTipService.SetToolTip(login, _platform.StartupDescription);
        });
        login.Click += async (_, _) => await RunAsync(async () =>
        {
            await _platform.SetEnabledAsync(login.IsChecked == true, default);
            login.IsChecked = await _platform.IsEnabledAsync(default);
            ToolTipService.SetToolTip(login, _platform.StartupDescription);
        });

        general.Children.Add(Label("Open History shortcut"));
        var shortcut = new TextBox
        {
            IsReadOnly = true, PlaceholderText = "Press shortcut",
            Text = _preferences.Shortcut is { } saved ? FormatShortcut(saved) : "",
            Width = 315
        };
        AutomationProperties.SetName(shortcut, "Record Open History shortcut");
        var shortcutRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        shortcutRow.Children.Add(shortcut);
        shortcutRow.Children.Add(Button("Clear", async () =>
        {
            await _platform.UnregisterAsync(default);
            _preferences = _preferences with { Shortcut = null };
            await SaveAsync();
            shortcut.Text = "";
        }));
        general.Children.Add(shortcutRow);
        var shortcutFeedback = Label("");
        shortcutFeedback.FontSize = 12;
        shortcutFeedback.Visibility = Visibility.Collapsed;
        general.Children.Add(shortcutFeedback);
        shortcut.KeyDown += async (_, e) =>
        {
            var key = e.Key.ToString();
            if (key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0])) return;
            var commandPressed = Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);
            // Cmd+W is the standard macOS Close Window command. Let AppKit route
            // it to the window even while the shortcut recorder has focus.
            if (_platform.UsesCommandKey && commandPressed && key.Equals("W", StringComparison.OrdinalIgnoreCase))
                return;

            e.Handled = true;
            var modifiers = ShortcutModifiers.None;
            if (Down(VirtualKey.Control)) modifiers |= ShortcutModifiers.Control;
            if (Down(VirtualKey.Menu)) modifiers |= ShortcutModifiers.Alt;
            if (Down(VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
            if (commandPressed) modifiers |= ShortcutModifiers.Meta;
            var candidate = new GlobalShortcut(key, modifiers);
            try { await _platform.RegisterAsync(candidate, default); }
            catch
            {
                shortcutFeedback.Text = "Shortcut unavailable. Previous shortcut retained.";
                shortcutFeedback.Visibility = Visibility.Visible;
                return;
            }
            _preferences = _preferences with { Shortcut = candidate };
            shortcut.Text = FormatShortcut(candidate);
            shortcutFeedback.Visibility = Visibility.Collapsed;
            try { await SaveAsync(); }
            catch
            {
                shortcutFeedback.Text = "Shortcut active, but could not save it.";
                shortcutFeedback.Visibility = Visibility.Visible;
            }
        };

        var path = Label("History: " + DatabasePath);
        path.FontSize = 11;
        path.Opacity = 0.65;
        general.Children.Add(path);

        void Section(string name, StackPanel page) =>
            header.Children.Add(Button(name, () =>
            {
                scroll.Content = page;
                return Task.CompletedTask;
            }));

        Section("General", general);
        if (_platform.UsesCommandKey)
        {
            var permissionsPage = NewPage();
            var status = Label("");
            status.FontSize = 12;
            permissionsPage.Children.Add(status);
            var help = Label("Set Clipboard to Always Allow and enable Accessibility in macOS.");
            help.FontSize = 12;
            permissionsPage.Children.Add(help);

            async Task RefreshPermissionsAsync()
            {
                await _platform.GetClipboardAccessAsync(default);
                status.Text = _platform.PermissionDescription;
            }

            permissionsPage.Children.Add(Button("Request permissions…", async () =>
            {
                _platform.RequestClipboardAccess();
                _platform.RequestPasteAccess();
                _platform.OpenClipboardSettings();
                await RefreshPermissionsAsync();
            }));
            Section("Permissions", permissionsPage);
            window.Activated += (_, args) =>
            {
                if (args.WindowActivationState != CoreWindowActivationState.Deactivated)
                    _ = RunAsync(RefreshPermissionsAsync);
            };
        }

        scroll.Content = general;
        ShowWindow(window);
    }

    private void ShowAbout()
    {
        if (_about is not null) { ShowWindow(_about); return; }
        var body = new StackPanel { Padding = new Thickness(20), Spacing = 16 };
        body.Children.Add(Label("CSharpClipboard " + typeof(App).Assembly.GetName().Version));
        var result = Label("Local clipboard history for Windows and macOS.");
        body.Children.Add(result);
        body.Children.Add(Button("Check for updates", async () =>
        {
            result.Text = "Checking…";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var release = await new ReleaseChecker(client, typeof(App).Assembly.GetName().Version!).CheckAsync(default);
                result.Text = release.Version is null ? "Release not available"
                    : release.UpdateAvailable ? $"Newer version: {release.Version}" : "Current version";
            }
            catch { result.Text = "Update check unavailable."; }
        }));
        body.Children.Add(Button("Open Release Page", () =>
        { _platform.OpenUrl(ReleaseChecker.ReleasePage); return Task.CompletedTask; }));
        body.Children.Add(Label("Updates are installed manually. No clipboard history is sent."));
        _about = NewWindow("About CSharpClipboard", 460, 300, body);
        ShowWindow(_about);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closing = true;
        _instance.Activated -= RequestHistory;
        _platform.HistoryRequested -= RequestHistory;
        if (_invocation is not null) await _invocation.DisposeAsync();
        try { if (_capture is not null) await _capture.DisposeAsync(); }
        finally
        {
            try
            {
                if (_platform is IAsyncDisposable asyncPlatform)
                    await asyncPlatform.DisposeAsync();
                else _platform.Dispose();
            }
            finally
            {
                if (_clipboard is IDisposable d && !ReferenceEquals(d, _platform)) d.Dispose();
                if (_history is IAsyncDisposable h) await h.DisposeAsync();
                foreach (var window in _windows) window.Close();
                _visible.Clear();
                _toggle.Dispose();
            }
        }
        // SingleInstanceLease remains held by Program until Uno.RunAsync actually returns.
    }
}
