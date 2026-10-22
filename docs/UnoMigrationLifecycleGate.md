# Uno migration: stage-0 lifecycle gate

Status: **BLOCKED — awaiting interactive Windows/macOS evidence** (2026-10-22).

The migration specification requires proof of a windowless desktop host before replacing the Avalonia shell. A runnable, isolated experiment is in [spikes/UnoLifecycle](../spikes/UnoLifecycle/README.md). This is an **experimental probe**, not a production conversion or certification.

## Why stage 0 blocks the migration

CSharpClipboard is a persistent clipboard utility. A normal `Window.Close()` must not terminate its capture, tray/status item or global hotkey, while explicit Quit must terminate the Uno **native event loop** as well as managed resources. CSharpGit's managed `IHost.StopAsync()` does not establish this. The Windows clipboard adapter's own Win32 message pump is not the Uno host. macOS bridge calls must execute on the AppKit main thread, and the current synchronous `MacOSIntegration.Dispose()` can deadlock if called from the UI dispatcher.

Until evidence is collected, replacing `CSharpClipboard.App` would risk losing tray functionality or running duplicate clipboard monitors. Therefore the existing application and release workflow remain unchanged.

The first CI compile identified another migration constraint: Uno 6.7.30 declares \`AppWindow.Hide()\` but reports it as not implemented (\`Uno0001\`). The probe therefore exercises native Win32/AppKit hide operations. Their runtime behavior is **not verified**.

## Stage-0 verification matrix

| Required experiment | Windows 11 x64 | macOS 26+ arm64 |
| --- | --- | --- |
| Uno 6.7.30 project restores/builds | **CI Debug build passed** | **CI Debug build passed** |
| `RunAsync` continues with zero visible windows, UI dispatcher available | Not verified | Not verified |
| Create, hide, reopen and close the only window without native loop exit | Not verified | Not verified |
| Quit when a UI window was never created; loop returns and process exits | Not verified | Not verified |
| Native tray/status item callbacks while zero windows visible | Not implemented in probe | Not implemented in probe |
| Windows Vulkan and explicit software backend with DPI/resize | Not verified | N/A |
| AppKit main-thread bridge, status-click snapshot and callback delivery | N/A | Not implemented in probe |
| No-focus/no-flicker startup, Finder launch, no Dock/taskbar icon | Not verified | Not verified |
| Native Quit, managed cleanup, idempotent disposal and partial-start failure | Not verified | Not verified |

Automated restore and Debug builds passed on both platforms in [GitHub Actions run 39774162839](https://github.com/DimonSmart/CSharpClipboard/actions/runs/39774162839). This is compile-only evidence: neither job started a desktop session, exercised a tray/status item, or checked native loop shutdown. The minimal probe's optimized Release build is not validated; the initial Release attempt was blocked by Uno DevServer validation (UNOB0019). Mark a cell `Verified` only with OS version, execution procedure, observed result and a non-sensitive log/screenshot. Any failure must be resolved in the host/lifetime design **before stages 1–4**.

## Next integration decisions (not yet implemented)

1. Establish a persistent native loop owner on both platforms, or document a blocking Uno host limitation. A hidden lifetime window is permissible only if it never activates, flickers, appears in taskbar/Dock/Alt+Tab or steals focus, and is independently verified on macOS.
2. Add a *native* Windows notification-area adapter with Explorer restart recovery, while preserving the macOS Swift `NSStatusItem` bridge, and verify both without a user-visible Uno window.
3. Test macOS `MacOSIntegration.Dispose()` off-dispatcher vs on-dispatcher and replace any synchronous wait-on-self with a safe async lifetime contract.
4. Only after confirmed Quit without a history window, begin migrating `App` composition, Safe Paste invocation ownership and all XAML UI; preserve original SQLite, settings, login identity and IDD contracts.

No functional or packaging claims for the Uno application are made by this stage-0 commit.
