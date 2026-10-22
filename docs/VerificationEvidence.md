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

## History opening snapshot

IDD-0003 / IDD-0004: History loads rows at opening with no periodic refresh. Search uses the snapshot. Explicit pin/delete/clear updates existing entries without importing new ones. Regression: HistoryPresentationTests.Open_snapshot_excludes_new_entries_until_reopening_and_reflects_explicit_changes (Windows: passed, Services 42/42). Interactive Windows/macOS A15: not verified.

## Win32 native HWND and History pin/close regression (2026-10-22)

The Windows native hide code now resolves the real HWND from Uno's native Win32 window peer, instead of passing `WindowNative.GetWindowHandle` (an Uno AppWindow ID) to Win32. The lifecycle spike uses the same correct lookup. Window visibility is marked hidden only after a successful native hide; failures keep the logical state visible, allowing a retry. Reentrant deactivation cannot start a second native hide. Pin/unpin ignores overlapping actions on one entry and pending refreshes are replayed after an in-progress refresh.

Noninteractive regression coverage: `WindowVisibilityTests` exercises successful/failing/reentrant hide state transitions and extraction of `Hwnd` (excluding ID-only peers). **Interactive Windows 11 behavior remains not verified**; check History close-button hide/reopen, Pin/Unpin button and keyboard shortcuts, Settings/About/Preview closing, and tray/clipboard continuity on actual Windows before closing the lifecycle release gate.

## History window cannot reopen after native hide (2026-10-22)

User-reported interactive Windows regression: after opening History from the tray and closing it with the title-bar X, a subsequent tray click does not show the History window. Root cause: on Win32 Uno, `Window.Activate()` only invokes `SetActiveWindow` after the first show. Uno's `ShowCore()` runs only once, so it does not undo a native `SW_HIDE`.

The application now calls Win32 `SW_SHOW` using the actual Uno native HWND before `Window.Activate()` **only for previously shown and hidden Windows windows**. First activation still uses Uno's normal initialization and render path. The visibility tracker covers reopen ordering, repeated show, hide failures, show failures, and reentrancy. The isolated lifecycle spike now exercises explicit SW_SHOW on Win32 reopen as well.

Automated tests are noninteractive and do not prove native desktop acceptance. **Real Windows 11 acceptance remains not verified**: open via tray, close with X, reopen via tray 20 times; repeat with Escape, click outside and global shortcut; confirm Settings/About/Preview also reopen, focus behaves normally, clipboard capture continues, and Quit truly exits. Mark acceptance only after collecting this evidence.

## Windows 11 Notepad safe-paste target restoration (2026-10-22)

User-reported scenario: Notepad is active, global hotkey opens History, double-click on an entry is expected to paste back into the same editor. The UI already invokes Paste on the first row click and the invocation rejects the second action; the possible failure is the guarded native target restoration.

WindowsPasteTargetPort now waits up to 450 ms for the **same** foreground window, focused HWND and UI Automation RuntimeId to reappear after restoring Notepad. A different foreground application, different focus HWND, changed process, invalidated snapshot or untrusted desktop still refuses automated input. No blind focus setting, retries of keyboard injection, privilege escalation or clipboard rewriting are introduced. Partial SendInput delivery is now reported as unavailable, not shortcut sent. WindowsFocusRestoreWaiterTests cover slow restoration, changed focus, timeout, cancellation and partial input delivery.

**Interactive Windows 11 evidence remains not verified.** Acceptance scenario: focus Notepad editor and place caret, trigger configured global hotkey, double-click one history item, check that the exact clipboard entry appears at the original caret with no duplicate and Undo works. Repeat with one click, Enter, two keyboard layouts, and a modern Notepad RichEditD2DPT editor; confirm that selecting a different target, a password field, UAC/elevation or an inaccessible UIA element only copies, with no synthetic paste. Do not treat green CI as proof of this scenario.

## Windows activation handoff before hiding History (2026-10-22)

A native diagnostic reproduced a separate failure with an external harmless WinForms text field and an in-process native panel: hiding the foreground panel activated an unrelated window. The original snapshot was then correctly rejected, so no paste was dispatched. Waiting longer for the original field could not fix that activation change.

PasteService now restores the original target before hiding History, while the application still owns foreground activation. It hides the panel, writes the clipboard, restores/rechecks the same snapshot again and performs the existing final target/revision checks before one shortcut. A refusal before hiding is final for that selection and still copies; Copy Only never restores focus. Public ports and history data formats are unchanged.

An isolated Windows diagnostic confirmed the corrected handoff with the actual production WindowsPasteTargetPort: capture the external field, activate the native panel, restore before hide, hide, write, restore, dispatch, then read only the harmless test field to confirm exact insertion. The corrected run retained the original foreground window and confirmed actual insertion. Runs in which another window took focus before capture were excluded. This diagnostic does not exercise the Uno row click, tray or hotkey entry points and is not full application acceptance evidence.

PasteServiceTests cover the handoff order, a target change during clipboard writing, refusal without restore retry, Copy Only, clipboard races and duplicate actions. Repository fallback verification is `dotnet test CSharpClipboard.sln -c Release --no-restore` plus the Windows interactive native test project with both desktop/focus flags enabled; the application Release build is checked separately. The focused IDD-0005/IDD-0010 drift review preserves target identity, clipboard revision checks and single dispatch; ENG-0001/0002/0004/0006/0007 apply. macOS runtime and full Windows application acceptance remain **not verified**.

Implementation report:

| Field | Result |
| --- | --- |
| Mode | satisfy-current-intent |
| Specs used as intent | IDD-0005 and IDD-0010; IDD-0004 row selection context |
| Engineering rules applied | ENG-0001, ENG-0002, ENG-0004, ENG-0006, ENG-0007; structural validation passed; no Conditional rules apply |
| Behavior changed | Transfer activation before hiding History; the old hide-first ordering in IDD-0005 was updated before completion |
| Behavior preserved | Exact clipboard text, Copy Only, target/revision checks, one shortcut, duplicate rejection |
| Public contracts preserved | All platform interfaces and invocation ownership |
| Compatibility/data constraints | No migration or compatibility adapters; no persisted data changes |
| Code areas changed | PasteService selection orchestration |
| Tests changed | Existing order/refusal scenarios extended; handoff and change-during-write regression added |
| Verification result | 101 tests passed, zero skipped with both Windows desktop flags; Release app build passed with zero warnings/errors; diff whitespace check passed |
| Engineering conformance | Focused review found no mismatch; native APIs remain in platform adapters |
| Intent-drift-audit result | Focused handoff/identity/revision/single-dispatch review found no remaining implementation mismatch in changed scope; full application acceptance evidence missing |
| Remaining risks | Uno tray/hotkey/row-click acceptance, real Notepad/browser/VS Code targets, macOS runtime and packaging remain unverified for this change |
