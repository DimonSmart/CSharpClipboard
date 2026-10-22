using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CSharpClipboard.Core;
using CSharpClipboard.Services;
using CSharpClipboard.Storage;

namespace CSharpClipboard.App;

internal interface IDesktopShell : IAsyncDisposable { Task StartAsync(); }

internal sealed class DesktopShell<TSnapshot> : IDesktopShell, IHistoryPanelPort where TSnapshot : class, IPasteTargetSnapshot
{
    private readonly IClipboardPort _clipboard;
    private readonly IClipboardWritePort _writer;
    private readonly IPasteTargetPort<TSnapshot> _targets;
    private readonly IDesktopPlatformServices _platform;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly HistoryPresentation _presentation = new();
    private readonly SemaphoreSlim _toggle = new(1, 1);
    private readonly DispatcherTimer _refresh;
    private IHistoryRepository? _history;
    private IHistoryMaintenance? _maintenance;
    private ClipboardCaptureCoordinator? _capture;
    private PasteService<TSnapshot>? _paste;
    private HistoryInvocation<TSnapshot>? _invocation;
    private JsonSettingsStore? _settings;
    private volatile AppPreferences _preferences = new();
    private SingleInstanceLease? _instance;
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseMenu, _trayStatus;
    private Window? _panel, _settingsWindow, _preview, _feedback, _about;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Watermark = "Search history…" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 };
    private readonly TextBlock _empty = new() { Text = "Copy text to start your history", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private int _searchVersion;
    private bool _refreshing, _closing;
    private bool _contextMenuOpen;
    private int _disposed;
    internal Window? HistoryWindow => _panel;
    internal Window? SettingsWindow => _settingsWindow;
    internal Window? AboutWindow => _about;

    public DesktopShell(IClipboardPort clipboard, IClipboardWritePort writer, IPasteTargetPort<TSnapshot> targets,
        IDesktopPlatformServices platform, IClassicDesktopStyleApplicationLifetime desktop, SingleInstanceLease? instance = null)
    {
        _clipboard = clipboard; _writer = writer; _targets = targets; _platform = platform; _desktop = desktop;
        _instance = instance;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += async (_, _) => await RunAsync(RefreshAsync);
    }

    public async Task StartAsync()
    {
        _instance ??= await SingleInstanceLease.AcquireAsync(_platform.DataDirectory);
        if (_instance is null) { _desktop.TryShutdown(); return; }
        _settings = new JsonSettingsStore(Path.Combine(_platform.DataDirectory, "settings.json"));
        _preferences = await _settings.GetAsync<AppPreferences>("preferences", default) ?? new AppPreferences();
        if (_preferences.Version != 1) throw new InvalidDataException("Settings version is newer than this application. Settings have been preserved.");
        var storage = await Task.Run(async () => await SqliteHistoryRepository.OpenAsync(DatabasePath, new HistoryRepositoryOptions { MaximumUnpinnedItems = Math.Clamp(_preferences.Retention, 50, 2000) }));
        _history = storage; _maintenance = storage;
        _capture = new ClipboardCaptureCoordinator(_clipboard, _history) { IsSourceExcluded = source => _preferences.ExcludedApplications.Contains(source, _platform.UsesCommandKey ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase) };
        _capture.StatusChanged += message => Dispatcher.UIThread.Post(() => _status.Text = message);
        if (_preferences.CapturePaused) await _capture.PauseAsync(); else await _capture.StartAsync();
        _paste = new PasteService<TSnapshot>(_targets, this, _clipboard, _writer, _capture);
        _instance.Activated += RequestHistory;
        _platform.HistoryRequested += RequestHistory;
        await CreateTrayAsync();
        if (_preferences.Shortcut is { } shortcut) await RunAsync(async () => await _platform.RegisterAsync(shortcut, default));
        if (!_preferences.Onboarded)
        {
            ShowSettings();
            _preferences = _preferences with { Onboarded = true };
            await SaveAsync();
        }
        _refresh.Start();
    }

    private string DatabasePath => Path.Combine(_platform.DataDirectory, "history.sqlite");
    private string Modifier => _platform.UsesCommandKey ? "Cmd" : "Ctrl";
    private void RequestHistory() => Dispatcher.UIThread.Post(() => _ = RunAsync(ToggleHistoryAsync));
    private async Task CreateTrayAsync()
    {
        if (_platform is IStatusItemPlatform nativeStatusItem)
        {
            await nativeStatusItem.CreateStatusItemAsync(OnStatusItemAction, _preferences.CapturePaused, default);
            return;
        }

        var menu = new NativeMenu();
        NativeMenuItem Item(string label, Action action) { var item = new NativeMenuItem(label); item.Click += (_, _) => action(); menu.Items.Add(item); return item; }
        Item("Clipboard History", RequestHistory);
        _pauseMenu = Item(_preferences.CapturePaused ? "Resume Capture" : "Pause Capture", () => _ = RunAsync(TogglePauseAsync));
        _trayStatus = new NativeMenuItem("Copy Only available") { IsEnabled = false }; menu.Items.Add(_trayStatus);
        Item("Settings…", ShowSettings);
        Item("Clear Recent History", () => _ = RunAsync(ClearRecentAsync));
        Item("About…", ShowAbout);
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Quit", () => _desktop.TryShutdown());
        _tray = new TrayIcon { Icon = AppIcon.Create(), ToolTipText = "CSharpClipboard", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => RequestHistory();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
    }

    private void OnStatusItemAction(StatusItemAction action) =>
        Dispatcher.UIThread.Post(() => _ = RunAsync(() => HandleStatusItemActionAsync(action)));

    private async Task HandleStatusItemActionAsync(StatusItemAction action)
    {
        switch (action)
        {
            case StatusItemAction.OpenHistory:
                await ToggleHistoryAsync();
                break;
            case StatusItemAction.ToggleCapture:
                await TogglePauseAsync();
                break;
            case StatusItemAction.Settings:
                ShowSettings();
                break;
            case StatusItemAction.ClearRecentHistory:
                await ClearRecentAsync();
                break;
            case StatusItemAction.About:
                ShowAbout();
                break;
            case StatusItemAction.Quit:
                _desktop.TryShutdown();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    internal async Task ToggleHistoryAsync()
    {
        if (!await _toggle.WaitAsync(0)) return;
        try
        {
            if (_panel?.IsVisible == true)
            {
                if (_platform is IStatusItemPlatform nativeStatusItem)
                    await nativeStatusItem.DiscardPendingStatusItemSnapshotAsync(default);
                await CloseHistoryAsync();
                return;
            }
            if (_invocation is not null) { await _invocation.DisposeAsync(); _invocation = null; }
            _invocation = await _paste!.OpenHistoryAsync();
        }
        finally { _toggle.Release(); }
    }

    public async ValueTask ShowAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var entries = await Task.Run(async () => await _history!.GetEntriesAsync(null, cancellationToken), cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EnsurePanel(); _presentation.Open(entries); _search.Text = ""; RenderRows(); PlacePanel();
            _panel!.Show(); _panel.Activate();
            if (_list.ItemCount > 0) { _list.SelectedIndex = 0; _list.Focus(); } else _search.Focus();
        });
    }
    public async ValueTask HideAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() => _panel?.Hide());
    }
    internal async Task CloseHistoryAsync()
    {
        _panel?.Hide();
        if (_invocation is { } invocation) { _invocation = null; await invocation.DisposeAsync(); }
    }
    private void EnsurePanel()
    {
        if (_panel is not null) return;
        _panel = new Window { Title = "Clipboard History", Width = 640, Height = 500, MinWidth = 320, MinHeight = 260,
            ShowInTaskbar = false, Icon = AppIcon.Create() };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 10 };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        AutomationProperties.SetName(_search, "Search history");
        searchRow.Children.Add(_search);
        var clear = Button("Clear", async () => { await Task.Run(async () => await _history!.ClearRecentAsync(default)); await Task.Run(() => _maintenance!.MaintainAsync()); await RefreshAsync(); }); Grid.SetColumn(clear, 1); searchRow.Children.Add(clear);
        Grid.SetRow(searchRow, 0); root.Children.Add(searchRow);
        var items = new Grid(); items.Children.Add(_list); items.Children.Add(_empty); Grid.SetRow(items, 1); root.Children.Add(items);
        AutomationProperties.SetName(_list, "Clipboard history entries");
        _list.ItemTemplate = new FuncDataTemplate<HistoryEntry>((entry, _) => entry is null ? new Border() : CreateRow(entry));
        var footer = new StackPanel { Spacing = 2 };
        footer.Children.Add(_status);
        var footerActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        footerActions.Children.Add(new TextBlock { Text = $"Enter Paste · {Modifier}+Enter Copy · {Modifier}+P Pin · {Modifier}+Y Preview · Escape Close", FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 });
        var settings = Button("⚙", () => { ShowSettings(); return Task.CompletedTask; });
        settings.FontSize = 18;
        settings.Padding = new Thickness(8, 4);
        settings.MinWidth = 32;
        settings.MinHeight = 32;
        AutomationProperties.SetName(settings, "Settings");
        ToolTip.SetTip(settings, "Settings");
        settings.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(settings, 1);
        footerActions.Children.Add(settings);
        footer.Children.Add(footerActions);
        Grid.SetRow(footer, 2); root.Children.Add(footer); _panel.Content = root;
        _panel.Closing += (_, e) => { if (!_closing) { e.Cancel = true; _ = RunAsync(CloseHistoryAsync); } };
        _panel.Deactivated += (_, _) => { if (_preview?.IsVisible != true && !_contextMenuOpen) _ = RunAsync(CloseHistoryAsync); };
        _panel.AddHandler(InputElement.KeyDownEvent, PanelKeyDown, RoutingStrategies.Tunnel);
        _search.TextChanged += async (_, _) =>
        {
            var version = ++_searchVersion;
            await Task.Delay(100);
            if (version == _searchVersion) await UpdateRowsAsync();
        };
    }
    private Control CreateRow(HistoryEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var preview = Regex.Replace(entry.Text.Length > 700 ? entry.Text[..700] : entry.Text, @"\s+", " ");
        var text = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, MaxHeight = 64, VerticalAlignment = VerticalAlignment.Center };
        text.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; _ = RunAsync(() => SelectAsync(entry, false)); } };
        grid.Children.Add(text);
        var pinAction = entry.PinnedAt is null ? "Pin" : "Unpin";
        var pin = Button(pinAction, () => PinAsync(entry));
        pin.Content = new PathIcon
        {
            Data = StreamGeometry.Parse("M16 9V4h1V2H7v2h1v5l-2 2v2h5v9h2v-9h5v-2l-2-2z"),
            Width = 16,
            Height = 16
        };
        AutomationProperties.SetName(pin, pinAction);
        ToolTip.SetTip(pin, pinAction);
        Grid.SetColumn(pin, 1); grid.Children.Add(pin);
        var border = new Border { Child = grid, Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 2) };
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _contextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
            Dispatcher.UIThread.Post(() => { if (_panel?.IsActive != true && _preview?.IsVisible != true) _ = RunAsync(CloseHistoryAsync); });
        };
        void Item(string name, Func<Task> action) { var item = new MenuItem { Header = name }; item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item); }
        Item("Paste", () => SelectAsync(entry, false)); Item("Copy Only", () => SelectAsync(entry, true));
        Item(entry.PinnedAt is null ? "Pin" : "Unpin", () => PinAsync(entry)); Item("Preview", () => PreviewAsync(entry));
        Item("Delete", () => DeleteAsync(entry));
        border.ContextMenu = menu;
        return border;
    }
    private async void PanelKeyDown(object? sender, KeyEventArgs e)
    {
        var command = _platform.UsesCommandKey ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.Escape) { e.Handled = true; await RunAsync(CloseHistoryAsync); return; }
        if (_list.SelectedItem is not HistoryEntry entry) return;
        Func<Task>? action = e.Key switch
        {
            Key.Enter => () => SelectAsync(entry, e.KeyModifiers.HasFlag(command)),
            Key.P when e.KeyModifiers.HasFlag(command) => () => PinAsync(entry),
            Key.Y when e.KeyModifiers.HasFlag(command) => () => PreviewAsync(entry),
            Key.Delete when e.KeyModifiers.HasFlag(command) => () => DeleteAsync(entry),
            Key.Back when _platform.UsesCommandKey && e.KeyModifiers.HasFlag(command) => () => DeleteAsync(entry),
            _ => null
        };
        if (action is not null) { e.Handled = true; await RunAsync(action); }
    }
    private async Task SelectAsync(HistoryEntry entry, bool copy)
    {
        if (_invocation is not { } invocation) return;
        var outcome = await _paste!.SelectAsync(invocation, entry.Text, copy ? HistorySelectionAction.CopyOnly : HistorySelectionAction.Paste);
        _status.Text = outcome switch
        {
            HistorySelectionOutcome.CopiedOnly => $"Copied. Press {Modifier}+V to paste.",
            HistorySelectionOutcome.ShortcutSent => "Paste shortcut sent.",
            HistorySelectionOutcome.ClipboardChanged => "Clipboard changed. Paste was cancelled.",
            HistorySelectionOutcome.RejectedDuplicate => _status.Text,
            _ => "Could not complete the action. History is preserved."
        };
        if (outcome != HistorySelectionOutcome.RejectedDuplicate) ShowActionFeedback(_status.Text ?? "Action complete");
    }
    private async void ShowActionFeedback(string message)
    {
        _feedback?.Close();
        var window = new Window { Title = "CSharpClipboard", ShowActivated = false, ShowInTaskbar = false, CanResize = false,
            Width = 420, Height = 90, SystemDecorations = SystemDecorations.None,
            Content = new Border { Padding = new Thickness(16), Child = Label(message) } };
        _feedback = window;
        var point = _platform.GetActivationPoint();
        var screen = window.Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) ?? window.Screens.Primary;
        if (screen is not null) window.Position = new PixelPoint(screen.WorkingArea.Right - (int)(440 * screen.Scaling), screen.WorkingArea.Bottom - (int)(110 * screen.Scaling));
        window.Show();
        await Task.Delay(3000);
        window.Close();
        if (ReferenceEquals(_feedback, window)) _feedback = null;
    }
    private async Task PinAsync(HistoryEntry entry)
    {
        await Task.Run(async () => { if (entry.PinnedAt is null) await _history!.PinAsync(entry.Id, default); else await _history!.UnpinAsync(entry.Id, default); });
        await RefreshAsync();
    }
    private async Task DeleteAsync(HistoryEntry entry) { await Task.Run(async () => await _history!.DeleteAsync(entry.Id, default)); await RefreshAsync(); }
    private async Task PreviewAsync(HistoryEntry entry)
    {
        if (_panel is null) return;
        _preview = new Window { Title = "Full Preview — Read Only", Width = 640, Height = 480,
            Content = new TextBox { Text = entry.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) } };
        await _preview.ShowDialog(_panel);
        _preview = null;
    }
    private void RenderRows()
    {
        var rows = _presentation.Filter(_search.Text ?? "");
        ApplyRows(rows);
    }
    private async Task UpdateRowsAsync()
    {
        var version = ++_searchVersion;
        var query = _search.Text ?? "";
        var rows = await Task.Run(() => _presentation.Filter(query));
        if (version == _searchVersion && !_closing) ApplyRows(rows);
    }
    private void ApplyRows(IReadOnlyList<HistoryEntry> rows)
    {
        var selected = (_list.SelectedItem as HistoryEntry)?.Id;
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(entry => entry.Id == selected) ?? rows.FirstOrDefault();
        _empty.IsVisible = rows.Count == 0;
        _empty.Text = string.IsNullOrEmpty(_search.Text) ? "Copy text to start your history" : "No matches";
    }
    private async Task RefreshAsync()
    {
        if (_refreshing || _history is null) return;
        _refreshing = true;
        try
        {
            if (_panel?.IsVisible == true)
            {
                var entries = await Task.Run(async () => await _history.GetEntriesAsync(null, default));
                _presentation.Refresh(entries); await UpdateRowsAsync();
                PlacePanel(false);
            }
            var access = await _platform.GetClipboardAccessAsync(default);
            if (_trayStatus is not null) _trayStatus.Header = _preferences.CapturePaused ? "Capture paused" : access == PermissionStatus.Allowed ? "Capture active · Copy Only available" : "Clipboard Access Required · Copy Only available";
            _tray!.ToolTipText = _preferences.CapturePaused ? "CSharpClipboard — Capture paused" : access == PermissionStatus.Allowed ? "CSharpClipboard — Capture active" : "CSharpClipboard — Clipboard Access Required";
            if (string.IsNullOrEmpty(_status.Text)) _status.Text = _preferences.CapturePaused ? "Capture paused" : access == PermissionStatus.Allowed ? "Capture active · Copy Only available · Paste checks the original field" : "Clipboard Access Required · Copy Only available";
        }
        finally { _refreshing = false; }
    }
    private void PlacePanel(bool center = true)
    {
        if (_panel is null) return;
        var point = _platform.GetActivationPoint();
        var screen = _panel.Screens.ScreenFromPoint(center ? new PixelPoint(point.X, point.Y) : _panel.Position) ?? _panel.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea; var scale = screen.Scaling;
        _panel.Width = Math.Min(640, area.Width / scale); _panel.Height = Math.Min(500, area.Height / scale);
        var width = (int)(_panel.Width * scale); var height = (int)(_panel.Height * scale);
        _panel.Position = center ? new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2) :
            new PixelPoint(Math.Clamp(_panel.Position.X, area.X, Math.Max(area.X, area.Right - width)), Math.Clamp(_panel.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }
    private async Task TogglePauseAsync()
    {
        var pause = !_preferences.CapturePaused;
        if (pause) await _capture!.PauseAsync(); else await _capture!.ResumeAsync();
        _preferences = _preferences with { CapturePaused = pause }; await SaveAsync();
        if (_pauseMenu is not null) _pauseMenu.Header = pause ? "Resume Capture" : "Pause Capture";
        if (_platform is IStatusItemPlatform nativeStatusItem)
            await nativeStatusItem.SetStatusItemCapturePausedAsync(pause, default);
        _status.Text = pause ? "Capture paused" : "Capture active";
    }
    private Task SaveAsync() => _settings!.SetAsync("preferences", _preferences, default).AsTask();
    private async Task ClearRecentAsync() { await Task.Run(async () => await _history!.ClearRecentAsync(default)); await Task.Run(() => _maintenance!.MaintainAsync()); await RefreshAsync(); }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { _status.Text = $"Operation unavailable ({error.GetType().Name}). History is preserved."; }
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        var window = new Window { Title = "CSharpClipboard Settings", Width = 620, Height = 650, Icon = AppIcon.Create() };
        _settingsWindow = window;
        var tabs = new TabControl();
        StackPanel Page(string name)
        {
            var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { Content = content } }); return content;
        }
        var general = Page("General");
        general.Children.Add(Label("Clipboard history stays on this device. Copy text, then open History from the tray."));
        general.Children.Add(Button("Open History", () => { window.Hide(); RequestHistory(); return Task.CompletedTask; }));
        general.Children.Add(Button("Pause / Resume Capture", TogglePauseAsync));
        general.Children.Add(Label("Retention: 50–2000 recent items. Pins are kept separately."));
        var retention = new NumericUpDown { Minimum = 50, Maximum = 2000, Value = _preferences.Retention, Increment = 50 };
        AutomationProperties.SetName(retention, "Recent history limit"); general.Children.Add(retention);
        general.Children.Add(Button("Apply retention", async () => { var count = (int)(retention.Value ?? 500); await Task.Run(() => _maintenance!.SetRetentionAsync(count)); _preferences = _preferences with { Retention = count }; await SaveAsync(); }));
        var login = new CheckBox { Content = "Launch at Login" }; general.Children.Add(login);
        var startupStatus = Label(_platform.StartupDescription);
        _ = RunAsync(async () => { login.IsChecked = await _platform.IsEnabledAsync(default); startupStatus.Text = _platform.StartupDescription; });
        login.Click += async (_, _) => await RunAsync(async () => { await _platform.SetEnabledAsync(login.IsChecked == true, default); login.IsChecked = await _platform.IsEnabledAsync(default); startupStatus.Text = _platform.StartupDescription; });
        general.Children.Add(startupStatus);
        general.Children.Add(Label("Open History shortcut (focus the box and press a combination):"));
        var shortcut = new TextBox { IsReadOnly = true, Watermark = "Not assigned", Text = _preferences.Shortcut is { } saved ? $"{saved.Modifiers}+{saved.Key}" : "" };
        AutomationProperties.SetName(shortcut, "Record Open History shortcut"); general.Children.Add(shortcut);
        var feedback = Label(""); general.Children.Add(feedback);
        shortcut.KeyDown += async (_, e) =>
        {
            e.Handled = true;
            if (e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9)
            {
                var modifiers = ShortcutModifiers.None;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ShortcutModifiers.Control;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ShortcutModifiers.Alt;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ShortcutModifiers.Shift;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= ShortcutModifiers.Meta;
                var candidate = new GlobalShortcut(e.Key.ToString().Replace("D", "", StringComparison.Ordinal) is { Length: > 0 } key ? key : "D", modifiers);
                try { await _platform.RegisterAsync(candidate, default); }
                catch { feedback.Text = "Shortcut unavailable or reserved. Previous shortcut is still active."; return; }
                _preferences = _preferences with { Shortcut = candidate };
                shortcut.Text = $"{modifiers}+{candidate.Key}";
                try { await SaveAsync(); feedback.Text = "Shortcut saved"; }
                catch { feedback.Text = "Shortcut is active, but settings could not be saved."; }
            }
        };
        general.Children.Add(Button("Remove shortcut", async () => { await _platform.UnregisterAsync(default); _preferences = _preferences with { Shortcut = null }; await SaveAsync(); shortcut.Text = ""; }));
        var privacy = Page("Privacy"); privacy.Children.Add(Label(_platform.PermissionDescription));
        var permissions = Label(_platform.PermissionDescription); privacy.Children.Add(permissions);
        privacy.Children.Add(Button("Refresh permission status", async () => { await _platform.GetClipboardAccessAsync(default); permissions.Text = _platform.PermissionDescription; }));
        if (_platform.UsesCommandKey)
        {
            privacy.Children.Add(Button("Request Clipboard Access Once", () => { _platform.RequestClipboardAccess(); return Task.CompletedTask; }));
            privacy.Children.Add(Label("If the system link does not open Clipboard settings, use System Settings → Privacy & Security → Clipboard → CSharpClipboard → Always Allow."));
            privacy.Children.Add(Button("Open Clipboard Privacy Settings", () => { _platform.OpenClipboardSettings(); return Task.CompletedTask; }));
            privacy.Children.Add(Button("Enable Accessibility for Automatic Paste…", () => { _platform.RequestPasteAccess(); return Task.CompletedTask; }));
        }
        privacy.Children.Add(Label("Ignore NEW copies from these exact app identifiers (one per line). Attribution is best-effort; exclusions cannot protect every secret."));
        var exclusions = new TextBox { Text = string.Join(Environment.NewLine, _preferences.ExcludedApplications), AcceptsReturn = true, MinHeight = 130, Watermark = _platform.UsesCommandKey ? "com.apple.Safari" : @"C:\Program Files\Example\example.exe" };
        privacy.Children.Add(exclusions);
        var exclusionStatus = Label(""); privacy.Children.Add(exclusionStatus);
        privacy.Children.Add(Button("Save exclusions", async () =>
        {
            var ids = (exclusions.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
            if (ids.Any(id => !_platform.IsValidApplicationIdentifier(id))) { exclusionStatus.Text = "Invalid application identifier. Check the example."; return; }
            _preferences = _preferences with { ExcludedApplications = ids }; await SaveAsync(); exclusionStatus.Text = "Exclusions saved";
        }));
        var data = Page("Data"); data.Children.Add(Label("History is stored unencrypted on this device. The operating system and your backups remain under your control."));
        data.Children.Add(new TextBox { Text = DatabasePath, IsReadOnly = true });
        data.Children.Add(Button("Clear Recent (keep pins)", async () => { await Task.Run(async () => await _history!.ClearRecentAsync(default)); await Task.Run(() => _maintenance!.MaintainAsync()); await RefreshAsync(); }));
        data.Children.Add(Button("Clear All…", async () => { if (await ConfirmAsync(window)) { await Task.Run(async () => await _history!.ClearAllAsync(default)); await Task.Run(() => _maintenance!.MaintainAsync()); await RefreshAsync(); } }));
        data.Children.Add(Label("Copy changes can occur faster than the system exposes them. CSharpClipboard cannot guarantee observing every copy."));
        window.Content = tabs; window.Show();
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private async Task<bool> ConfirmAsync(Window owner)
    {
        var dialog = new Window { Title = "Delete all clipboard history?", Width = 420, Height = 180, CanResize = false };
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        stack.Children.Add(Label("Delete all entries, including pins? This cannot be undone."));
        stack.Children.Add(Button("Cancel", () => { dialog.Close(false); return Task.CompletedTask; }));
        stack.Children.Add(Button("Delete all history", () => { dialog.Close(true); return Task.CompletedTask; }));
        dialog.Content = stack; return await dialog.ShowDialog<bool>(owner);
    }
    private void ShowAbout()
    {
        var window = new Window { Title = "About CSharpClipboard", Width = 460, Height = 300, Icon = AppIcon.Create() };
        _about = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_about, window)) _about = null; };
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        content.Children.Add(Label("CSharpClipboard " + typeof(App).Assembly.GetName().Version));
        var result = Label("Local clipboard history for Windows and macOS."); content.Children.Add(result);
        content.Children.Add(Button("Check for updates", async () =>
        {
            result.Text = "Checking…";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var release = await new ReleaseChecker(client, typeof(App).Assembly.GetName().Version!).CheckAsync(default);
                result.Text = release.Version is null ? "Release not available" : release.UpdateAvailable ? $"Newer version: {release.Version}" : "Current version";
            }
            catch { result.Text = "Update check not available"; }
        }));
        content.Children.Add(Button("Open Release Page", () => { _platform.OpenUrl(ReleaseChecker.ReleasePage); return Task.CompletedTask; }));
        content.Children.Add(Label("Updates are downloaded and installed manually. No history or search data is sent."));
        window.Content = content; window.Show();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closing = true; _refresh.Stop(); _tray?.Dispose();
        _platform.HistoryRequested -= RequestHistory;
        _panel?.Close(); _settingsWindow?.Close(); _preview?.Close(); _feedback?.Close(); _about?.Close();
        if (_invocation is not null) await _invocation.DisposeAsync();
        try { if (_capture is not null) await _capture.DisposeAsync(); }
        finally
        {
            try { _platform.Dispose(); }
            finally
            {
                try { if (_clipboard is IDisposable disposable) disposable.Dispose(); }
                finally { try { if (_history is IAsyncDisposable history) await history.DisposeAsync(); } finally { _instance?.Dispose(); } }
            }
        }
    }
}
