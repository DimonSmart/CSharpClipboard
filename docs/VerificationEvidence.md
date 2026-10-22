# Verification evidence — Uno migration (2026-10-22)

This file reflects the **current Uno UI implementation**, not the pre-migration binary. Older native/desktop smoke results for previous commits do not prove that the new Uno host works.

## Confirmed in CI

[GitHub Actions 39778882061](https://github.com/DimonSmart/CSharpClipboard/actions/runs/39778882061):

- Windows `windows-latest`: solution restore, Release build and full noninteractive test suite succeeded; self-contained Windows ZIP packaging succeeded.
- macOS `macos-26`: solution restore, Release build and full noninteractive test suite succeeded; native Swift bridge compilation, self-contained app packaging, signing validation, `--version`, and `--bridge-check` succeeded.
- UI project uses Uno.Sdk 6.7.30 with Skia Desktop, Vulkan by default on Win32 and an explicit `--software-rendering` selection.
- Core/Services/Storage and OS adapters are separate .NET 10 libraries with no Uno dependency.
- Direct references to the old UI toolkit were removed from source/project files; an additional CI audit checks the complete restored dependency graph and source.
- Native Windows notification-area implementation is in `WindowsTrayIcon`; macOS continues to use the Swift `NSStatusItem` bridge.

## Important known gaps

1. **No interactive Windows/macOS session smoke for the new Uno UI**. Windowless host lifetime, tray mouse actions, 20x hide/show, hotkeys, Safe Paste original target, Win32 Vulkan resize, AppKit main thread and TCC, Finder launch, multi-monitor positioning, DPI and focus behavior are **not verified**. A passing compiler and package script are not substitutes.
2. Uno 6.7.30 reports `AppWindow.Hide()` as unsupported; the UI uses Win32 `ShowWindow(SW_HIDE)` / AppKit `NSWindow.orderOut:`. The OS behavior still needs real-device checks.
3. The Uno SDK currently pulls a DevServer target into optimized Release builds in this repository (`UNOB0019`); the UI project temporarily uses `Optimize=false` even in Release. Fix the SDK configuration and rerun release+package validation before shipping to end users.
4. Earlier toolkit-specific headless UI tests were removed. They must be replaced with Uno-aware UI/integration tests rather than treating the smaller test suite as equivalent coverage.
5. The macOS ad-hoc signature is for development. Production Developer ID signing, notarization, TCC and login registration continuity are not certified.

Do not claim migration acceptance criteria or safe public release as verified until the platform-specific tests have been executed and recorded. The [acceptance matrix](Acceptance.md) and [lifecycle matrix](UnoMigrationLifecycleGate.md) remain release gates.
