# Changelog

## 1.0.0 - 2026-08-13

FrameLock's first public release provides exact Windows client-area sizing for recording, streaming, demos, screenshots, and screen sharing.

### Highlights

- Discover visible desktop application windows with their names and icons.
- Lock a target's content area to common landscape and portrait presets or a custom resolution.
- Correct unwanted resizes using WinEvent notifications with a low-frequency safety watchdog.
- Center the target on its current monitor and restore its original placement when unlocking.
- Handle minimized, closed, or replaced target windows without restoring an unrelated HWND.
- Remember the last resolution and per-application choices locally.
- Follow Windows light/dark appearance at launch and support keyboard and high-contrast use.

### Distribution

- The release is a framework-dependent Windows build and requires the .NET 10 Desktop Runtime.
- Windows 10 version 1809 or newer and Windows 11 are supported; x64 Windows is the validated v1 environment.
- v1.0.0 does not include an installer, code signing, or automatic updates.
