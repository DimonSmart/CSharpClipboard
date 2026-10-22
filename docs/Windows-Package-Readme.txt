CSharpClipboard for Windows 11 x64

Extract this ZIP to a folder under your user profile, then run CSharpClipboard.App.exe.
The application appears in the notification area. Left click opens Clipboard History;
right click opens the menu. Settings is shown on first launch. No .NET SDK is needed.

Enter or a row click: guarded Paste. Ctrl+Enter: Copy Only. Ctrl+P: Pin.
Ctrl+Y: full read-only Preview. Ctrl+Delete: Delete. Escape: close without an action.
Use Settings to assign an Open History shortcut; none is assigned by default.
Quit from the tray before replacing or moving application files.

History and settings: %LOCALAPPDATA%\CSharpClipboard. History is not encrypted.
Clear Recent preserves pins. Clear All requires confirmation. To remove this portable
application, first disable Launch at Login, Quit, and remove its extracted folder.
Deleting the separate data folder removes saved history/settings permanently.

Launch at Login uses HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CSharpClipboard.
Exclusions use exact full executable paths (case-insensitive), best-effort clipboard owner
attribution. Unknown owners can still be captured. Exclusions cannot guarantee secrecy.
Pause prevents clipboard content reads. Resume does not import the current old item.

Automatic paste requires proof of the original focused field. Elevated, protected,
remote, changed or unsupported targets use Copy Only. A dispatched paste shortcut does
not prove that the target accepted insertion. The chosen text remains on the clipboard.
Embedded NUL is preserved in private exact-text data; ordinary CF_UNICODETEXT consumers
may stop at the first NUL, as required by that system format.

This development package is unsigned unless separately Authenticode-signed by its publisher.
Checksums verify file integrity, not publisher identity. About only opens release pages;
it never installs an untrusted downloaded package. No automatic network checks or telemetry.
