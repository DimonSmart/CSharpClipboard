namespace CSharpClipboard.Core;

public readonly record struct ClipboardRevision(string Value)
{
    public static ClipboardRevision None { get; } = new(string.Empty);
}

public sealed record ClipboardSignal(ClipboardRevision Revision);

[Flags]
public enum ClipboardContentFlags
{
    None = 0,
    ContainsFiles = 1,
    Composite = 2,
    ProtectedOrExcluded = 4,
    Transient = 8,
    Generated = 16
}

public sealed record ClipboardSource(string Identifier);

public sealed record ClipboardReadResult(
    ClipboardRevision Revision,
    int ItemCount,
    string? PlainText,
    ClipboardSource? Source,
    ClipboardContentFlags ContentFlags);

public sealed record HistoryCapture(string Text, ClipboardSource? Source);

public sealed record HistoryEntry(
    Guid Id,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset CopiedAt,
    ClipboardSource? Source,
    DateTimeOffset? PinnedAt);

public interface IHistoryRepository
{
    ValueTask UpsertCaptureAsync(HistoryCapture capture, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<HistoryEntry>> GetEntriesAsync(string? searchText, CancellationToken cancellationToken);

    ValueTask<bool> PinAsync(Guid id, CancellationToken cancellationToken);

    ValueTask<bool> UnpinAsync(Guid id, CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    ValueTask ClearRecentAsync(CancellationToken cancellationToken);

    ValueTask ClearAllAsync(CancellationToken cancellationToken);
}

public interface IHistoryMaintenance
{
    Task SetRetentionAsync(int maximum, CancellationToken cancellationToken = default);
    Task MaintainAsync(CancellationToken cancellationToken = default);
}

public interface IClipboardPort
{
    event EventHandler<ClipboardSignal>? Changed;

    ValueTask<ClipboardRevision> GetRevisionAsync(CancellationToken cancellationToken);

    ValueTask<ClipboardReadResult> ReadAsync(ClipboardRevision expectedRevision, CancellationToken cancellationToken);
}

// An immutable, invocation-owned native snapshot. Adapters release native resources on disposal.
public interface IPasteTargetSnapshot : IAsyncDisposable
{
}

public interface IPasteTargetPort<TSnapshot>
    where TSnapshot : IPasteTargetSnapshot
{
    ValueTask<TSnapshot?> GetActiveTargetAsync(CancellationToken cancellationToken);

    // Restore only the original identity, never a replacement or a target deliberately left by the user.
    // Refuse unknown/protected/exited/changed targets, untrusted desktops and missing paste permission.
    ValueTask<bool> TryRestoreAsync(TSnapshot snapshot, CancellationToken cancellationToken);

    // On the native operation's required thread, freshly recheck identity, focused element,
    // foreground, protection, paste permission and expected revision immediately before one
    // normal paste shortcut. A prior service/restore check is insufficient. Always report
    // ClipboardChanged preferentially when the expected revision no longer matches.
    // Return ShortcutSent once input was dispatched, even if cancellation then arrives;
    // cancellation must not turn already dispatched input into a retryable failure.
    ValueTask<PasteDispatchResult> TryDispatchAsync(TSnapshot snapshot, ClipboardRevision expectedRevision,
        CancellationToken cancellationToken);
}

public enum PasteDispatchResult { ShortcutSent, TargetUnavailable, ClipboardChanged }

public interface IHistoryPanelPort
{
    ValueTask ShowAsync(Guid invocationId, CancellationToken cancellationToken);
    ValueTask HideAsync(Guid invocationId, CancellationToken cancellationToken);
}

public interface IClipboardWritePort
{
    // Write exact plain text with platform generated markers where supported. Return the
    // revision belonging to this commit, not a later read of an unrelated external revision.
    // Cancellation/failure may throw only before commit; after commit return its receipt.
    // Changed callbacks must only enqueue signals and must never await capture processing:
    // this operation runs within the capture queue until its receipt has been registered.
    ValueTask<ClipboardRevision> WriteTextAsync(string text, CancellationToken cancellationToken);
}

public interface IClipboardWriteCoordinator
{
    // Serializes the native write and receipt registration with capture reads/persistence.
    // Must not enable paused capture or reset its baseline to a separately queried revision.
    Task<ClipboardRevision> WriteTextAsync(IClipboardWritePort writer, string text,
        CancellationToken cancellationToken = default);
}

public sealed record GlobalShortcut(string Key, ShortcutModifiers Modifiers);

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8
}

public interface IGlobalShortcutPort
{
    ValueTask RegisterAsync(GlobalShortcut shortcut, CancellationToken cancellationToken);

    ValueTask UnregisterAsync(CancellationToken cancellationToken);
}

public interface IStartupRegistration
{
    ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken);

    ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

public enum PermissionStatus
{
    Allowed,
    RequiresAccess,
    Denied
}

public interface IPermissionStatusPort
{
    ValueTask<PermissionStatus> GetClipboardAccessAsync(CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken);

    ValueTask SetAsync<T>(string key, T value, CancellationToken cancellationToken);
}

public interface IPlatformIdentity
{
    string PlatformName { get; }

    string ApplicationIdentifier { get; }
}

public sealed record UpdateCheckResult(bool UpdateAvailable, string? Version);

public interface IAppUpdater
{
    ValueTask<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record AppPreferences
{
    public int Version { get; init; } = 1;
    public int Retention { get; init; } = 500;
    public bool CapturePaused { get; init; }
    public bool Onboarded { get; init; }
    public GlobalShortcut? Shortcut { get; init; }
    public string[] ExcludedApplications { get; init; } = [];
}

public interface IDesktopPlatformServices : IGlobalShortcutPort, IStartupRegistration, IPermissionStatusPort, IPlatformIdentity, IDisposable
{
    event Action? HistoryRequested;
    string DataDirectory { get; }
    bool UsesCommandKey { get; }
    string PermissionDescription { get; }
    string StartupDescription { get; }
    (int X, int Y) GetActivationPoint();
    void OpenUrl(string url);
    void RequestClipboardAccess();
    void RequestPasteAccess();
    void OpenClipboardSettings();
    bool IsValidApplicationIdentifier(string identifier);
}
