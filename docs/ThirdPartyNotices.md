# Dependency inventory

Runtime dependencies are centrally pinned in `Directory.Packages.props`. Packages are restored from NuGet. Package notices and original `.nuspec` metadata accompany the Windows package under `notices/`; preserve them when redistributing.

- Avalonia 11.3.10 and MicroCom.Runtime: MIT, AvaloniaUI contributors.
- Microsoft.Data.Sqlite 10.0.13 and .NET 10 runtime: MIT, .NET Foundation contributors; retain Microsoft's third-party notices.
- SQLitePCLRaw 2.1.13: Apache-2.0; SQLite native engine: public domain (see package notices).
- SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1: MIT bindings plus bundled upstream notices for Skia/HarfBuzz and dependencies.
- ANGLE native assets: upstream BSD notices.
- Tmds.DBus.Protocol 0.95.1: MIT. Present as an Avalonia platform dependency, not used to send history.

The application has no telemetry client. Its only application HTTP request is the user-triggered GitHub stable-release check. Avalonia.BuildServices build-task assets are explicitly excluded. No source text, preview, search, clipboard digest, or application source is submitted to a service. The test-only xUnit and Avalonia.Headless packages are not application runtime dependencies.

This inventory records package metadata rather than granting additional rights; original notices remain authoritative.
