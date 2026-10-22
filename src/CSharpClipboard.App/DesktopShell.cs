using System.Text.RegularExpressions;
using CSharpClipboard.Core;
using CSharpClipboard.Services;
using CSharpClipboard.Storage;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private readonly HashSet<Window> _visible = [];
    private readonly List<Window> _windows = [];
    private readonly SingleInstanceLease _instance;
    private IHistoryRepository? _history;
    private IHistoryMaintenance? _maintenance;
    private ClipboardCaptureCoordinator? _capture;
    private PasteService<TSnapshot>? _paste;
    private HistoryInvocation<TSnapshot>? _invocation;
    private JsonSettingsStore? _settings;
    private AppPreferences _preferences = new();
    private Window? _panel, _settingsWindow, _preview, _about;
    private HistoryView? _view;
    private int _searchVersion;
    private bool _refreshing;
    private bool _closing;
    private bool _contextMenuOpen;
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
            new HistoryRepositoryOptions { MaximumUnpinnedItems = Math.Clamp(_preferences.Retention, 50, 2000) }));
        _history = storage;
        _maintenance = storage;
        _capture = new ClipboardCaptureCoordinator(_clipboard, _history)
        {
            IsSourceExcluded = source => _preferences.ExcludedApplications.Contains(
                source, _platform.UsesCommandKey ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
        };
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
            _view!.SearchBox.Text = "";
            RenderRows();
            ShowWindow(_panel!);
            if (_view.HistoryList.Items.Count > 0)
            {
                _view.HistoryList.SelectedIndex = 0;
                _view.HistoryList.Focus(FocusState.Programmatic);
            }
            else _view.SearchBox.Focus(FocusState.Programmatic);
        });
    }

    public ValueTask HideAsync(Guid invocationId, CancellationToken cancellationToken) =>
        new(UiAsync(() => { if (_panel is not null) HideWindow(_panel); }));

    internal async Task CloseHistoryAsync()
    {
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
        _view.KeyboardHints.Text = $"Enter Paste · {Modifier}+Enter Copy · {Modifier}+P Pin · {Modifier}+Y Preview · Escape Close";
        _view.ClearButton.Click += async (_, _) => await RunAsync(ClearRecentAsync);
        _view.SettingsButton.Click += (_, _) => ShowSettings();
        _view.HistoryList.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ListViewItem item && item.Tag is HistoryEntry entry)
                _ = RunAsync(() => SelectAsync(entry, false));
        };
        _view.HistoryList.KeyDown += PanelKeyDown;
        _view.SearchBox.KeyDown += PanelKeyDown;
        _view.SearchBox.TextChanged += async (_, _) =>
        {
            var version = ++_searchVersion;
            await Task.Delay(100);
            if (version == _searchVersion) await RunAsync(UpdateRowsAsync);
        };
        _panel.Activated += (_, args) =>
        {
            // Deactivation may be caused by an intentional context menu or preview.
            if (args.WindowActivationState == CoreWindowActivationState.Deactivated &&
                !_contextMenuOpen && !IsShown(_preview) && !_closing && IsShown(_panel))
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
            if (onHide is not null) onHide();
            else HideWindow(window);
        };
        _windows.Add(window);
        return window;
    }

    private void ShowWindow(Window window) { _visible.Add(window); window.Activate(); }
    private void HideWindow(Window window)
    {
        if (_visible.Remove(window)) NativeWindowVisibility.Hide(window);
    }
    private bool IsShown(Window? window) => window is not null && _visible.Contains(window);

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
            var pin = new Button { Content = "📌" };
            pin.Click += (_, _) => _ = RunAsync(() => PinAsync(entry));
            AutomationProperties.SetName(pin, pinLabel);
            ToolTipService.SetToolTip(pin, pinLabel);
            Grid.SetColumn(pin, 1);
            layout.Children.Add(pin);
            item.Content = layout;
            var menu = new MenuFlyout();
            MenuItem(menu, "Paste", () => SelectAsync(entry, false));
            MenuItem(menu, "Copy Only", () => SelectAsync(entry, true));
            MenuItem(menu, pinLabel, () => PinAsync(entry));
            MenuItem(menu, "Preview", () => PreviewAsync(entry));
            MenuItem(menu, "Delete", () => DeleteAsync(entry));
            item.ContextFlyout = menu;
            menu.Opening += (_, _) => _contextMenuOpen = true;
            menu.Closed += (_, _) => _contextMenuOpen = false;
            list.Items.Add(item);
            if (entry.Id == selected) list.SelectedItem = item;
        }
        if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0;
        _view.EmptyMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.EmptyMessage.Text = string.IsNullOrEmpty(_view.SearchBox.Text) ? "Copy text to start your history" : "No matches";
    }

    private static void MenuItem(MenuFlyout menu, string caption, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = caption };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private async void PanelKeyDown(object sender, KeyRoutedEventArgs e)
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
            VirtualKey.Y when command => () => PreviewAsync(entry),
            VirtualKey.Delete when command => () => DeleteAsync(entry),
            VirtualKey.Back when command && _platform.UsesCommandKey => () => DeleteAsync(entry),
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
        await Task.Delay(3000);
        Post(() => { if (ReferenceEquals(_view, view)) view.FeedbackLabel.Text = ""; });
    }

    private async Task PinAsync(HistoryEntry entry)
    {
        await Task.Run(async () =>
        {
            if (entry.PinnedAt is null) await _history!.PinAsync(entry.Id, default);
            else await _history!.UnpinAsync(entry.Id, default);
        });
        await RefreshAsync();
    }
    private async Task DeleteAsync(HistoryEntry entry)
    {
        await Task.Run(async () => await _history!.DeleteAsync(entry.Id, default));
        await RefreshAsync();
    }

    private Task PreviewAsync(HistoryEntry entry)
    {
        if (_preview is not null) HideWindow(_preview);
        _preview = NewWindow("Full Preview — Read Only", 640, 480,
            new TextBox
            {
                Text = entry.Text, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12)
            });
        ShowWindow(_preview);
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _history is null || _closing) return;
        _refreshing = true;
        try
        {
            if (IsShown(_panel))
            {
                var entries = await Task.Run(async () => await _history.GetEntriesAsync(null, default));
                _presentation.Refresh(entries);
                await UpdateRowsAsync();
            }
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
        var window = NewWindow("CSharpClipboard Settings", 620, 650, root);
        _settingsWindow = window;

        StackPanel NewPage() => new() { Padding = new Thickness(20), Spacing = 12 };
        var general = NewPage();
        general.Children.Add(Label("Clipboard history stays on this device. Open History from the tray."));
        general.Children.Add(Button("Open History", () =>
        {
            HideWindow(window);
            RequestHistory();
            return Task.CompletedTask;
        }));
        general.Children.Add(Button("Pause / Resume Capture", TogglePauseAsync));
        general.Children.Add(Label("Retention: 50–2000 recent items. Pins are kept separately."));
        var retention = new TextBox { Text = _preferences.Retention.ToString(), InputScope = new InputScope() };
        AutomationProperties.SetName(retention, "Recent history limit");
        general.Children.Add(retention);
        var retentionStatus = Label("");
        general.Children.Add(Button("Apply retention", async () =>
        {
            if (!int.TryParse(retention.Text, out var count) || count is < 50 or > 2000)
            { retentionStatus.Text = "Enter a number from 50 to 2000."; return; }
            await Task.Run(() => _maintenance!.SetRetentionAsync(count));
            _preferences = _preferences with { Retention = count };
            await SaveAsync();
            retentionStatus.Text = "Retention saved.";
        }));
        general.Children.Add(retentionStatus);
        var login = new CheckBox { Content = "Launch at Login" };
        general.Children.Add(login);
        var startup = Label(_platform.StartupDescription);
        general.Children.Add(startup);
        _ = RunAsync(async () =>
        {
            login.IsChecked = await _platform.IsEnabledAsync(default);
            startup.Text = _platform.StartupDescription;
        });
        login.Click += async (_, _) => await RunAsync(async () =>
        {
            await _platform.SetEnabledAsync(login.IsChecked == true, default);
            login.IsChecked = await _platform.IsEnabledAsync(default);
            startup.Text = _platform.StartupDescription;
        });
        general.Children.Add(Label("Open History shortcut (Ctrl/Alt/Shift on Windows; Cmd/Alt/Shift on macOS):"));
        var shortcut = new TextBox
        {
            IsReadOnly = true, PlaceholderText = "Focus and press a shortcut",
            Text = _preferences.Shortcut is { } saved ? $"{saved.Modifiers}+{saved.Key}" : ""
        };
        AutomationProperties.SetName(shortcut, "Record Open History shortcut");
        general.Children.Add(shortcut);
        var shortcutFeedback = Label("");
        general.Children.Add(shortcutFeedback);
        shortcut.KeyDown += async (_, e) =>
        {
            e.Handled = true;
            var key = e.Key.ToString();
            if (key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0])) return;
            var modifiers = ShortcutModifiers.None;
            if (Down(VirtualKey.Control)) modifiers |= ShortcutModifiers.Control;
            if (Down(VirtualKey.Menu)) modifiers |= ShortcutModifiers.Alt;
            if (Down(VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
            if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= ShortcutModifiers.Meta;
            var candidate = new GlobalShortcut(key, modifiers);
            try { await _platform.RegisterAsync(candidate, default); }
            catch { shortcutFeedback.Text = "Shortcut unavailable or reserved. Previous shortcut retained."; return; }
            _preferences = _preferences with { Shortcut = candidate };
            shortcut.Text = $"{candidate.Modifiers}+{candidate.Key}";
            try { await SaveAsync(); shortcutFeedback.Text = "Shortcut saved."; }
            catch { shortcutFeedback.Text = "Shortcut active; settings could not be saved."; }
        };
        general.Children.Add(Button("Remove shortcut", async () =>
        {
            await _platform.UnregisterAsync(default);
            _preferences = _preferences with { Shortcut = null };
            await SaveAsync();
            shortcut.Text = "";
        }));

        var privacy = NewPage();
        var permissions = Label(_platform.PermissionDescription);
        privacy.Children.Add(permissions);
        privacy.Children.Add(Button("Refresh permission status", async () =>
        {
            await _platform.GetClipboardAccessAsync(default);
            permissions.Text = _platform.PermissionDescription;
        }));
        if (_platform.UsesCommandKey)
        {
            privacy.Children.Add(Button("Request Clipboard Access Once", () =>
            { _platform.RequestClipboardAccess(); return Task.CompletedTask; }));
            privacy.Children.Add(Label("If the system link fails, use System Settings → Privacy & Security → Clipboard → CSharpClipboard → Always Allow."));
            privacy.Children.Add(Button("Open Clipboard Privacy Settings", () =>
            { _platform.OpenClipboardSettings(); return Task.CompletedTask; }));
            privacy.Children.Add(Button("Enable Accessibility for Automatic Paste…", () =>
            { _platform.RequestPasteAccess(); return Task.CompletedTask; }));
        }
        privacy.Children.Add(Label("Ignore NEW copies from these exact app identifiers (one per line). Attribution is best-effort."));
        var exclusions = new TextBox
        {
            Text = string.Join(Environment.NewLine, _preferences.ExcludedApplications),
            AcceptsReturn = true, MinHeight = 130,
            PlaceholderText = _platform.UsesCommandKey ? "com.apple.Safari" : @"C:\Program Files\Example\example.exe"
        };
        privacy.Children.Add(exclusions);
        var exclusionStatus = Label("");
        privacy.Children.Add(exclusionStatus);
        privacy.Children.Add(Button("Save exclusions", async () =>
        {
            var ids = (exclusions.Text ?? "").Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
            if (ids.Any(id => !_platform.IsValidApplicationIdentifier(id)))
            { exclusionStatus.Text = "Invalid application identifier."; return; }
            _preferences = _preferences with { ExcludedApplications = ids };
            await SaveAsync();
            exclusionStatus.Text = "Exclusions saved.";
        }));

        var data = NewPage();
        data.Children.Add(Label("History is stored unencrypted on this device."));
        data.Children.Add(new TextBox { Text = DatabasePath, IsReadOnly = true });
        data.Children.Add(Button("Clear Recent (keep pins)", ClearRecentAsync));
        data.Children.Add(Button("Clear All…", async () =>
        {
            if (await ConfirmAsync(window))
            {
                await Task.Run(async () => await _history!.ClearAllAsync(default));
                await Task.Run(() => _maintenance!.MaintainAsync());
                await RefreshAsync();
            }
        }));
        data.Children.Add(Label("Copy events can occur faster than the OS exposes them; not every copy is guaranteed to be observed."));

        void Section(string name, StackPanel page)
        {
            header.Children.Add(Button(name, () =>
            {
                scroll.Content = page;
                return Task.CompletedTask;
            }));
        }
        Section("General", general);
        Section("Privacy", privacy);
        Section("Data", data);
        scroll.Content = general;
        ShowWindow(window);
    }

    private static async Task<bool> ConfirmAsync(Window owner)
    {
        if (owner.Content is not FrameworkElement root || root.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "Delete all clipboard history?",
            Content = "Delete every entry, including pins? This cannot be undone.",
            PrimaryButtonText = "Delete all history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
