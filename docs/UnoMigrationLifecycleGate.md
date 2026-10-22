# Uno migration: desktop lifecycle and release gate

Status: **CODE MIGRATED; INTERACTIVE ACCEPTANCE NOT VERIFIED** (2026-10-22).

`CSharpClipboard.App` now uses Uno Platform + Skia Desktop, and Windows tray is handled by a Win32 adapter. The original backend is no longer a compile/runtime dependency. The isolated original host probe is retained under [spikes/UnoLifecycle](../spikes/UnoLifecycle/README.md).

Build, noninteractive tests and both packaging workflows passed in [CI run 39778882061](https://github.com/DimonSmart/CSharpClipboard/actions/runs/39778882061). These are **not proof** that an app without visible Uno windows remains alive or that a native tray callback can reactivate a hidden Uno window.

## Mandatory interactive acceptance

| Experiment | Windows 11 x64 | macOS 26+ arm64 |
| --- | --- | --- |
| Launch without visible UI or stolen focus; tray/status item, clipboard baseline active | Not verified | Not verified |
| Open/close/reopen History 20x without native loop shutdown, duplicate capture, or leaks | Not verified | Not verified |
| Quit without ever opening a history window | Not verified | Not verified |
| Quit with History, Settings and Preview open; idempotent native + managed shutdown | Not verified | Not verified |
| Hotkey and second-launch IPC before/after History; immutable original paste target | Not verified | Not verified |
| Tray left/right click and menu; Explorer restart / macOS status click snapshot | Not verified | Not verified |
| Clipboard permissions, Accessibility, safe paste target and focus refusal | Not verified | Not verified |
| Windows Vulkan and explicit software renderer; DPI, resize and multi-monitor placement | Not verified | N/A |
| Finder launch without Dock icon, fullscreen Spaces, bundled dylib and TCC persistence | N/A | Not verified |
| Login registration, SQLite/settings upgrade and installed-app package smoke | Not verified | Not verified |

Stage 0 was a deliberately blocking risk gate. The subsequent implementation was requested explicitly, but **no runtime acceptance evidence has been collected**. Keep the release gate open.

The Uno SDK's optimized Release/DevServer conflict is temporarily bypassed using `Optimize=false` in the UI project. That workaround must be revisited before production deployment. The old UI headless tests also need an equivalent Uno-aware replacement.
