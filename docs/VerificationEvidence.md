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

## Actual Windows panel / Notepad acceptance attempt (2026-10-22)

Tested the Release executable through Windows Computer Use against a separate initially empty modern Notepad window. Only a harmless synthetic marker was entered/copied; existing Notepad documents were not edited.

The existing profile could not start: the real application displayed `Startup unavailable` with `SqliteException`. A read-only SQLite `quick_check` independently failed with `database disk image is malformed`. This prevents normal-profile tray/hotkey acceptance. The cause of database corruption was not established by this check.

For isolation, the original database and sidecars were temporarily moved aside without deletion, the exact settings bytes were backed up, and a temporary Ctrl+Alt+Shift+H registration was used with a test history. The genuine global hotkey opened the genuine Uno `Clipboard History` panel, and the copied marker was visible as a row. This establishes opening and capture, not insertion.

The attempted row click was refused by Computer Use because the point was over another foreground application. Refresh showed that History had hidden after losing activation. No successful click or resulting Notepad insertion was established. Retrying the complete focus-sensitive scenario requires an uninterrupted desktop session.

| Scenario | Result |
| --- | --- |
| Start with existing history | Failed: malformed SQLite database |
| Capture harmless Notepad text into test history | Passed: marker visible in real History list |
| Open real panel using registered global hotkey | Passed on isolated test history |
| Select row and insert back into original Notepad field | Not verified: foreground changed before click |
| Open from notification-area icon and insert into Notepad | Not verified: the available Computer Use window inventory did not expose a targetable taskbar/tray surface |

The temporary application was stopped, original history/sidecars returned, and exact original settings restored (no assigned hotkey). The isolated test history was preserved under `%TEMP%/CSharpClipboard-e2e-run-20261022`. No implementation changes were made during this acceptance attempt. Full Windows end-to-end acceptance remains **not verified**.

## SQLite corruption investigation and profile reset (2026-10-22)

The user authorized discarding the affected history. Offline examination found a 16,384-byte file with 4,096-byte pages (four actual pages), but the database header at offset 28 declared 49 pages. Header change-counter and version-valid-for both equalled 3; schema version was 1. A temporary diagnostic copy with only the page-count field corrected (and rollback-mode header flags for opening the standalone copy) passed `PRAGMA integrity_check`. The profile was not repaired by patching raw header bytes.

**The origin of that incorrect page count remains unproven.** The maintenance/shutdown corruption hypothesis was tested, not confirmed. No application code outside SQLite writes database pages. The shipped Windows x64 native SQLite reports 3.53.3; this is newer than the [3.51.3 fix for the WAL-reset corruption bug](https://www.sqlite.org/releaselog/3_51_3.html), so that old bug is not an established explanation. No speculative dependency or WAL-policy change was made.

The existing maintenance integration test now repeats fill/retention/pin/clear/VACUUM, disposes and reopens the repository, and runs full SQLite integrity checking for three cycles. All passed. An additional temporary process probe used the production repository to commit a pinned synthetic baseline, repeatedly fill/clear/maintain, then was forcibly terminated 0, 1, 3, 10, 30 and 100 ms after signalling the maintenance boundary. Every restart passed `integrity_check` and retained the committed pinned baseline. These six timings exercise abrupt termination around maintenance; they do not prove power-loss durability or explain the original damaged file.

Startup now performs `quick_check` before migration/configuration. SQLite corruption/not-a-database errors and non-ok integrity results become a content-free `HistoryDatabaseCorruptException`; the UI explains that the database is damaged and how to restore/recreate it while stopped. Corrupt input is never automatically deleted or silently replaced. Regression tests inject both the observed oversized header count and an invalid digest-index page, verify rejection, unchanged file bytes, released connection handles and no stored text in the exposed exception.

With the application stopped, only the authorized profile `history.sqlite`, `history.sqlite-wal` and `history.sqlite-shm` were removed. Preferences were unchanged. The updated Release executable created a new schema-v1 database; an independent read-only `integrity_check` returned `ok`. A second launch exposed a native window titled `Clipboard History` rather than `Startup unavailable`. Computer Use could enumerate that window, but activation/capture was unreliable, so this does not upgrade the earlier tray/hotkey/paste acceptance status.

| Implementation report | Result |
| --- | --- |
| Mode | satisfy-current-intent |
| Specs used as intent | IDD-0003 durability/versioning; IDD-0011 understandable, private diagnostics |
| Engineering rules applied | Always ENG-0001/0002/0004/0006/0007; Conditional ENG-0003; structure checked |
| Behavior changed | Integrity validation and explicit damaged-database startup explanation |
| Behavior preserved | No automatic data deletion; exact BLOB storage, transactions, WAL/FULL, schema version and rejection of unknown future versions |
| Public contracts preserved | Repository/platform interfaces and persisted schema unchanged; one explicit corruption exception added |
| Compatibility/data constraints | No compatibility adapter or migration; one-time profile reset explicitly authorized by user |
| Code areas changed | Repository open validation and startup error presentation |
| Tests changed | Two corruption regressions; existing maintenance scenario extended across restart/cycles/integrity checking |
| Verification result | Release app build: zero warnings/errors; full solution: 99 passed, 4 interactive Windows tests skipped; storage subset: all 13 passed; six temporary crash probes passed |
| Engineering conformance | Focused review found no mismatch; SQL remains in Storage, native UI in platform/UI layers, no content-bearing corruption diagnostics |
| Intent-drift-audit result | Focused storage startup/maintenance review conforms to IDD-0003 and IDD-0011; no product-intent change required |
| Remaining risks | Exact initiating cause unproven; macOS runtime and full Windows paste acceptance remain unverified |

## Limited Windows console-target identification fallback (2026-10-23)

The Windows paste target now retains strict UIA RuntimeId checks for ordinary fields. For known terminal hosts only (classic `conhost`/`OpenConsole` with `ConsoleWindowClass`, Windows Terminal with `CASCADIA_HOSTING_WINDOW_CLASS`), unavailable UIA identity may fall back to checking the same PID, process start, foreground HWND, GUI thread, focused HWND within the window, executable name, and window class. A positively reported UIA password or disabled element still refuses automatic paste. Existing trusted desktop, integrity, clipboard-revision and single-dispatch safeguards remain unchanged. No simulated typing or paste shortcut changes were introduced.

Unit tests cover matching class/process pairs and lookalike/unknown hosts. Interactive end-to-end confirmation in Windows Terminal and classic conhost is **not verified**. In particular, tab/pane switches within a terminal that retain the same focused HWND cannot be distinguished by window-level identity alone; verify this limitation and treat as an unresolved safety constraint for such targets. The Windows host must be tested with real non-elevated console windows and full hotkey → UI selection → paste flow before considering this acceptance complete.
