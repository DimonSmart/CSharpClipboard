# Uno desktop lifecycle spike (not production)

Isolated **stage 0** experiment for the [CSharpClipboard Uno migration](../../docs/UnoMigrationLifecycleGate.md). No dependency on, and no changes to, the existing Avalonia application. Do not add this project to `CSharpClipboard.sln` until the lifecycle gate is resolved.

## Running on a real Windows/macOS desktop

Use .NET 10 SDK (10.0.204 or a newer .NET 10 feature band) and run from this directory, which pins Uno.Sdk 6.7.30:

```sh
dotnet restore UnoLifecycleProbe.csproj
dotnet run --project UnoLifecycleProbe.csproj -c Release
dotnet run --project UnoLifecycleProbe.csproj -c Release -- --no-window
dotnet run --project UnoLifecycleProbe.csproj -c Release -- --start-visible
```

On Windows additionally test explicit software rendering:

```powershell
dotnet run --project UnoLifecycleProbe.csproj -c Release -- --software
```

The default sequence begins with **no window**, then attempts to create, hide, reopen, close and quit the only window at roughly 2.5, 4.5, 6.5, 8.5 and 10.5 seconds. The `--no-window` mode attempts Quit with no window ever created. `--start-visible` is a positive control with manual Hide/Close/Quit buttons (if Hide is pressed, close via the OS or repeat an automated run). All events are recorded in `CSharpClipboard-UnoLifecycle-<PID>.log` under the OS temporary directory.

A missing `OnLaunched`, dispatcher callback, or `RunAsync returned` is a **failure** to investigate, not proof of success. Likewise, an early `RunAsync returned` before the scheduled explicit Quit demonstrates an unsuitable windowless lifetime. Do not treat compilation/CI as desktop lifecycle verification.

This sample deliberately contains **no native tray/status-item, bridge calls, clipboard monitoring, single-instance lock, or SQLite**. Those are separate required stage-0 checks. In particular, headless/offscreen Uno hosting is **not** an acceptable substitute for a real Win32/AppKit tray utility. Do not move to UI migration until every stage-0 check in the linked gate passes on both platforms.
