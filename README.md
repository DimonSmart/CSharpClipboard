# CSharpClipboard

Local plain-text clipboard history for Windows 11 x64 and macOS 26+ arm64, built with .NET 10, Avalonia and SQLite. Windows is implemented and tested here; native macOS compilation, permissions and Finder behavior require the macOS CI/device checks described in [Verification evidence](docs/VerificationEvidence.md).

Extract the Windows portable ZIP and run `CSharpClipboard.App.exe`. It starts in the notification area; Settings appears on first launch. No SDK is required by the packaged application.

Left click opens Clipboard History; right click offers History, Pause/Resume Capture, Settings, Clear Recent, About and Quit. Closing a window keeps capture running. A second launch opens History in the existing instance.

## History and privacy

Search, pin, preview, delete and clear local text history. Clear Recent preserves pins; Clear All asks for confirmation. Retention is configurable from 50 to 2000 recent entries, initially 500. History remains in a private user directory and is not encrypted. Pause performs no content reads; Resume establishes a new baseline without importing the previous clipboard item. Application exclusions are best-effort: exact full executable paths on Windows, exact bundle IDs on macOS.

Enter/row click requests guarded Paste. Ctrl+Enter (Cmd+Enter on Mac) is Copy Only. Ctrl/Cmd+P pins, Ctrl/Cmd+Y previews, Ctrl+Delete on Windows / Cmd+Backspace on Mac deletes, Escape closes without action. Open History hotkey is unassigned initially and recorded in Settings.

Paste checks the original process, window, field, permissions and clipboard revision before sending one normal paste shortcut. Unsupported, protected, elevated/remote or changed targets use Copy Only. The clipboard is never restored to an old value; a later external change cancels Paste. `Shortcut sent` is not a guarantee the target accepted insertion. Embedded NUL is retained in storage/private exact-text data; ordinary Windows Unicode-text consumers naturally stop at the first NUL.

About performs a user-initiated stable GitHub Release check and opens the release page. It does not install downloaded archives. There are no automatic update checks or telemetry.

## Build and test

```powershell
dotnet restore CSharpClipboard.sln
dotnet test CSharpClipboard.sln -c Release
dotnet run --project src/CSharpClipboard.App
./scripts/package-windows.ps1
```

Interactive Windows native checks use a harmless test field and back up supported clipboard formats. Run from an interactive desktop, with no clipboard-changing application running:

```powershell
$env:CSHARPCLIPBOARD_DESKTOP_TESTS = '1'
$env:CSHARPCLIPBOARD_FOCUS_TESTS = '1'
dotnet test tests/CSharpClipboard.Platform.Windows.Tests -c Release
```

On macOS with .NET 10 and Xcode 26+:

```sh
bash scripts/package-macos.sh
bash scripts/run-macos-dev-app.sh
```

The scripts produce a stable `dev.dimonsmart.CSharpClipboard` app bundle with nested ad-hoc signatures. This is a development identity, not Developer ID publisher authentication/notarization. Use the normal System Settings approval flow for a downloaded app; the scripts never disable Gatekeeper. Re-grant Clipboard and Accessibility permissions to the new application as needed. `dotnet run` does not prove Finder/TCC behavior. Clipboard Always Allow and Accessibility are independent permissions.

Windows Launch at Login uses per-user HKCU Run registration; disable it before deleting/moving a portable installation. macOS uses SMAppService and reports actual approval status. The GitHub workflow builds/tests both platforms and creates artifacts; uploading this code does not imply that CI has already passed.

See [acceptance matrix](docs/Acceptance.md), [evidence](docs/VerificationEvidence.md) and [dependency notices](docs/ThirdPartyNotices.md). `.idd/intent/` owns product behavior; `.idd/engineering/` owns implementation guardrails.
