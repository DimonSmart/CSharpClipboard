# Verification evidence — 2026-10-22

Implementation was continued directly by the primary agent after the user explicitly requested no further child agents. Product Intent and Engineering Rules were preserved. This report distinguishes implemented code, automated evidence and the remaining real-device release gate.

## Environment and checks

Windows 11 x64, build 26200; .NET SDK 10.0.402. The project has no `.idd/verification.yaml`, so direct implementation uses the .NET build/test fallback.

- Release solution build: zero warnings/errors.
- Full solution suite: 87 tests with the interactive Windows flags enabled (13 Core, 42 Services, 11 Storage, 13 Windows, 8 App).
- Interactive flags: `CSHARPCLIPBOARD_DESKTOP_TESTS=1`, `CSHARPCLIPBOARD_FOCUS_TESTS=1`. Native tests use harmless synthetic text, preserve HGLOBAL-backed clipboard formats, refuse unsafe backup formats, and do not restore over a newer external revision. Close clipboard-changing applications during these tests; initial clipboard backup has bounded contention retry.
- TRX output: `artifacts/test-results/`. CI normally skips opt-in desktop tests; that is not evidence of their execution on CI.
- Self-contained `win-x64` publish and portable ZIP packaging are provided by `scripts/package-windows.ps1`.
- Windows package smoke: the executable launched with an invalid `DOTNET_ROOT` and disabled multilevel lookup, opened first-run Settings, remained responsive, created the private data files, and a second process signalled the existing instance and exited with code 0. The smoke instance was stopped after inspection.
- Managed self-contained `osx-arm64` publish succeeds on Windows. The Swift/AppKit bridge cannot be compiled here: no Apple SDK/Xcode is installed. Its source and macOS build/link/sign/load checks are in the repository and CI workflow; those checks have not run on this machine.

### Final handoff recheck (2026-10-22)

- The earlier `delivery_*.trx` reports establish 87 passed, zero failed/skipped on the unchanged implementation (2026-10-21 23:19 host timestamps).
- A fresh interactive `completion_*.trx` run was **not successful**: 84 passed and three native clipboard tests refused setup before modifying clipboard data. A separate read-only diagnostic confirmed `OpenClipboard` fails with Win32 error 5 (access denied); no open-window owner could be identified. The cause of this session restriction is not established. This is an environment-limited recheck, not a new passing native run.
- `dotnet test CSharpClipboard.sln -c Release --no-restore` with both desktop opt-in flags disabled then completed successfully: **83 passed, four explicitly skipped**, zero failed. Reports use `completion-noninteractive_*.trx`. Restore/build emitted no warnings/errors.
- The final extracted ZIP executable remained responsive; a second launch exited with code 0. Its smoke process was stopped after inspection. Window activation was not independently confirmed in this final session.
- Final artifact: `artifacts/final/CSharpClipboard-1.0.0-win-x64.zip` (48,497,253 bytes). SHA-256: `73f4c59bd87240ea8474dfdd5ba76d66dd4c91963c4d4afaf199bd1add9ce370`, matching the supplied sidecar. No application binaries changed after packaging.

### Implementation handoff

Mode: `satisfy-current-intent`. Specs used: IDD-0001–0011, with IDD-0012–0014 retained as native evidence obligations. Applied Engineering Rules are listed in the audit below.

Changed behavior: complete tray/history/settings shell, capture and safe-paste integration, native Windows write/target/hotkey/startup adapters, macOS bridge source, private persistence and packaging. Preserved contracts: exact text storage, revision ownership, conservative CopyOnly outcomes, explicit user permissions/update checks and schema-v1 data handling. No compatibility layer or durable Intent/Engineering change was introduced.

Code areas: Core contracts, Services orchestration, Storage, both platform adapters, Avalonia App, native macOS bridge, packaging scripts and CI. Tests cover services/storage, actual headless controls, IPC and opt-in Win32 integration as detailed below. The requested implementation scope feasible on Windows is delivered; this is not certification of the remaining public-release/device acceptance matrix. Factory's native-agent workflow was superseded by the user's explicit instruction to finish directly without new agents; no fresh planner `# Done` or successful Factory final check is claimed.

## Concrete automated coverage

| Intent area | Concrete checks | Scope of evidence |
| --- | --- | --- |
| IDD-0001 lifecycle and capture baseline | `ClipboardCaptureCoordinatorTests.Start_and_resume_only_set_the_baseline`, `Pause_skips_signals_without_reading_or_writing`; `InfrastructureTests.Single_instance_signals_existing_owner_without_second_lease`; actual `DesktopShellTests` startup/close/cleanup | Unit, UI and IPC checks; packaged second-launch smoke |
| IDD-0002 revision/privacy/own writes | Core eligibility corpus, capture serialization/race tests, exact-source exclusion test; `WindowsDesktopIntegrationTests.Native_writer_receipt_roundtrips_exact_text_and_is_generated` and `Native_self_write_is_omitted_and_immediate_external_copy_is_captured` | Real Win32 clipboard plus deterministic capture timing; no macOS permission claim |
| IDD-0003 storage | `SqliteHistoryRepositoryTests`: real SQLite byte-exact Unicode/CRLF/NUL, digest candidate/full comparison, retention/pins, sorting/search, restart/delete/clear, rollback/busy/migration/newer-version refusal; `SettingsAndMaintenanceTests.Retention_changes_apply_immediately_and_clear_maintenance_reclaims_storage` | Real SQLite; maintenance checkpoints after VACUUM and reclaims the compacted file |
| IDD-0004 History | `DesktopShellTests.Actual_shell_keyboard_copy_and_cleanup` / `Actual_shell_keyboard_paste_and_cleanup`; `HistoryPresentationTests.Open_order_is_frozen_while_refresh_search_and_reopening_use_current_data` | Real Avalonia controls on headless backend, injected platform ports; desktop scaling/Narrator still manual |
| IDD-0005 safe Paste | 34 deterministic `PasteServiceTests` cases; `WindowsDesktopIntegrationTests.Native_focus_identity_allows_one_ctrl_v_and_refuses_changed_focus_and_revision` | Real Win32 edit receives harmless text through production SendInput. US, Russian and US Dvorak layouts are exercised. Changed field/revision and password-field snapshot refusal are checked. Tests attach only the test window thread to establish foreground; production never uses AttachThreadInput |
| IDD-0006 hotkeys | `WindowsDesktopIntegrationTests.Shortcut_conflict_keeps_the_previous_registration_and_unregister_releases_it` | Real RegisterHotKey conflict, previous registration retained, unregister, reserved Ctrl+V refusal |
| IDD-0007 settings | `SettingsAndMaintenanceTests.Atomic_settings_preserve_other_keys_and_refuse_corrupt_data_without_overwriting`; `DesktopShellTests.Corrupt_settings_preserve_file_and_shutdown_releases_platform_and_instance` | Atomic file replacement, preservation on invalid JSON, failed-start cleanup; Windows login/logout registration and macOS approvals need manual checks |
| IDD-0008 distribution/update | `InfrastructureTests.Release_check_accepts_only_new_stable_release`; Windows portable package smoke; managed macOS cross-publish | User-triggered stable-release check only; no download/install trust bypass. Windows package is unsigned; macOS package script explicitly labels ad-hoc identity |
| IDD-0009 Mac integration | `MacOSIntegration`, `native/macos/ClipboardBridge.swift`, build scripts and `--bridge-check` | Implementation supplied. Native compilation, Finder, TCC/AX, SMAppService, layouts and status item behavior remain **not verified** |
| IDD-0010 Windows integration | Native listener/read/dispose, writer, focus/layout/refusal and hotkey tests; per-user Run registration and private directory ACL in adapter | Real API coverage listed above. Elevated apps, secure desktop, RDP, multiple real editors and DPI/displays still require the acceptance matrix |
| IDD-0011 error/privacy/responsiveness | Failure/cancellation tests, JSON preservation, SQLite rollback/busy, startup error UI, state/type-only diagnostic search | No application clipboard-content logging or automatic HTTP/telemetry. Search and database work run off UI thread; macOS AppKit operations stay on main thread |

Native receipt testing found that Windows may finalize its sequence during `CloseClipboard`. The writer now attributes a stable post-publication revision only while its owner remains current. An intervening external owner yields a stale receipt and conservative Paste cancellation rather than mislabelling the external revision as a self-write.

## Remaining acceptance / release gate

These are missing verification, not automatically accepted exceptions:

- The complete real-target matrix: Notepad, browser forms, VS Code and Windows Terminal; elevated/UIPI, secure desktop/RDP, multiple monitors/DPI, Narrator, tray left/right clicks, and real login/logout registration. The native harmless-field test does not claim all third-party targets work.
- Every macOS installed-app check in IDD-0009 and spikes IDD-0012/0013/0014: clean Default/Ask/Allow/Deny, grant/revoke AX, no background prompts, Finder activation, native bridge load, ad-hoc identity continuity, layouts, Spaces/displays, and login approval. Managed cross-publish is not native evidence.
- The macOS bundle's managed-resource links, nested signatures, generated icon and installed launch must pass the supplied native CI and real-device checks. No Developer ID certificate/notarization pipeline is configured; the provided Mac ZIP is a development ad-hoc artifact.
- macOS floating-window fullscreen remains an explicit compatibility gap pending Avalonia/AppKit device verification. No fullscreen parity is claimed.
- CI workflow is supplied for `windows-latest` and `macos-26`; no remote CI execution or GitHub release has been performed by this task.

`docs/Acceptance.md` remains the full A01–A26 and AC checklist. A row marked not verified there has not been certified as a complete real-device scenario merely because its underlying implementation now exists.

## Focused Intent Drift Audit

Scope: the implemented desktop shell, Windows adapters, safe-paste/capture integration, settings/storage maintenance, release check, macOS bridge source and packaging (IDD-0001–0011; spikes 0012–0014 as evidence obligations).

Product Intent conformance: implemented behaviors have owners in the current specs. Conservative target fallback, exact storage, separate permissions, own-write suppression and explicit-user update checks match the inspected contract. Missing real-device evidence and the macOS fullscreen boundary above remain classified as `missing-verification` / `compatibility-boundary`; specs were not rewritten to declare them passed.

Engineering conformance: Always ENG-0001/0002/0004/0006/0007 and Conditional ENG-0003 (persisted history) / ENG-0005 (macOS packaging) apply. Core stays free of UI/native/storage dependencies; Services depends only on Core; native APIs stay in platform adapters. UI accesses history via Core contracts, and native snapshot ownership/disposal is explicit. NuGet versions are central; Avalonia.BuildServices task assets are excluded. C ABI version 1 is checked before Mac adapter use. Native bundle/signing and CI conformance still need their target-host execution evidence.

Recommended remaining work is the stated device/CI acceptance matrix before a public release, not additional mock evidence for OS permissions.
