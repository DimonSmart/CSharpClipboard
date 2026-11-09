# CSharpClipboard

Local plain-text clipboard history for Windows 11 x64 and macOS 26+ arm64, built with .NET 10, Uno Platform + Skia Desktop and SQLite. The UI migration requires interactive Windows/macOS verification; build-only results are insufficient. See [Uno migration verification](docs/UnoMigrationLifecycleGate.md).

Extract the Windows portable ZIP and run `CSharpClipboard.App.exe`. It starts in the notification area; Settings appears on first launch. No SDK is required by the packaged application.

Left click opens Clipboard History; right click offers History, Pause/Resume Capture, Settings, Clear Recent, About and Quit. Closing a window keeps capture running. A second launch opens History in the existing instance.

## History and permissions

Search and pin plain-text history. **Clear** removes unpinned entries and preserves pins. History does not have an automatic entry-count limit. History remains in a private user directory and is not encrypted. Clicking a history row requests guarded Paste. Ctrl+Enter (Cmd+Enter on Mac) uses Copy Only. Ctrl/Cmd+P toggles the selected pin; Escape closes History. No per-entry context menu, Preview, Delete or Data settings are shown.

Settings has Launch at Login, a compact Open History shortcut recorder and a small history-path label. macOS also has Permissions, with one button to request Clipboard and Accessibility access and open the system panel. Clipboard **Always Allow** is needed for automatic capture; Accessibility independently allows guarded paste. Windows does not need a Clipboard permission screen. Use the tray/status menu to pause or resume capture.

Paste checks the original process, window, field, permissions and clipboard revision before sending one normal paste shortcut. Unsupported, protected, elevated/remote or changed targets use Copy Only. A later external clipboard change cancels Paste. `Shortcut sent` is not a guarantee the target accepted insertion. Embedded NUL is retained in storage/private exact-text data; ordinary Windows Unicode-text consumers naturally stop at the first NUL.

About performs a user-initiated stable GitHub Release check and opens the release page. It does not install downloaded archives. There are no automatic update checks or telemetry.

## Build and test

```powershell
dotnet restore CSharpClipboard.sln
dotnet test CSharpClipboard.sln -c Release
dotnet run --project src/CSharpClipboard.App -c Debug
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

## Publishing

From a clean checkout, run `publish-next-version.bat` on Windows or `pwsh -NoProfile -File ./publish-next-version.ps1` on macOS/Linux. The script fetches `origin`, updates `main`, and pushes the next annotated `vMAJOR.MINOR.PATCH` tag; the first release starts at `v1.0.0`. Pushing a tag runs the release workflow, which builds and tests on Windows and macOS, then publishes a stable GitHub Release with Windows x64 and macOS arm64 packages plus SHA-256 checksums.

The Windows release ZIP is not Authenticode-signed. The macOS ZIP is ad-hoc signed and is not Developer ID signed or notarized. Checksums verify file integrity, not publisher identity; the app only opens the release page and does not install these packages.

Windows Launch at Login uses per-user HKCU Run registration; disable it before deleting/moving a portable installation. macOS uses SMAppService and reports actual approval status. Pull requests and pushes to `main` run cross-platform build, test, and package CI. A version tag also runs the release workflow; pushing code does not imply that CI has already passed.

See [acceptance matrix](docs/Acceptance.md), [evidence](docs/VerificationEvidence.md) and [dependency notices](docs/ThirdPartyNotices.md). `.idd/intent/` owns product behavior; `.idd/engineering/` owns implementation guardrails.
