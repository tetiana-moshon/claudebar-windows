# ClaudeBar for Windows

A Windows system-tray app that shows your Claude Code usage limits and tells you when it's safe to keep working. This is a native Windows (.NET 8 / WPF) port of the macOS [ClaudeBar](https://github.com/Alexandr-Kravchuk/claude-session-manager).

## Features

- Live usage for the 5-hour session window and the 7-day weekly windows (including the per-model weekly cap where your plan reports it)
- Pace projection: where each window is headed at reset, with deviation-band bars and headroom advice
- A glanceable tray icon colored by projected usage, showing session remaining %
- Context-aware recommendations — keep going, slow down, or when a window stabilizes
- A Statistics window: quota-burn chart over time, plus an activity heatmap of when you use Claude Code
- Launch at Login toggle
- Optional Windows toast notifications when a limit needs attention
- Optional auto-update from this repo's GitHub releases

## Requirements

- Windows 10 or 11 (x64)
- Claude Code logged in (`claude login`) — ClaudeBar reads the token the CLI stores
- To build from source: the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer)

The published release is self-contained: **no .NET runtime install is required** to run it.

## Install (from source)

```powershell
# Build a self-contained ClaudeBar.exe and zip it into dist\
./publish.ps1

# The exe is at publish\ClaudeBar.exe — copy it wherever you like and run it.
```

For development:

```powershell
./run.ps1     # build (Debug) and (re)launch in the system tray
```

To start ClaudeBar automatically at login, open the tray popover and tick **Launch at Login**
(this adds a per-user `HKCU\...\Run` entry — no admin rights needed).

## Uninstall

```powershell
# Stop it, remove the autostart entry, and delete stored data.
Get-Process ClaudeBar -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClaudeBar -ErrorAction SilentlyContinue
Remove-Item "$env:APPDATA\ClaudeBar" -Recurse -Force
# then delete the ClaudeBar.exe you copied
```

## Security & privacy

ClaudeBar touches your Claude credentials, so here is exactly what it does. This mirrors the
macOS app's behavior, adapted to how the tokens are stored on Windows:

- It reads the Claude Code CLI's credential file at `%USERPROFILE%\.claude\.credentials.json`
  (on Windows the CLI stores this as plaintext JSON rather than in a keychain). Only the
  **access token** under `claudeAiOauth` is parsed; the refresh token is ignored.
- If the Claude **desktop app** is installed, ClaudeBar can also read the access token it keeps
  continuously fresh, so ClaudeBar never has to refresh the (rate-limited) OAuth token endpoint
  itself. That token lives in `%APPDATA%\Claude\config.json`, encrypted with the Chromium
  `os_crypt` "v10" scheme (AES-256-GCM); the key is DPAPI-protected in `%APPDATA%\Claude\Local State`
  and is decrypted only for the current Windows user. If the desktop app isn't present, ClaudeBar
  simply uses the CLI token.
- The token is sent to exactly one place: `https://api.anthropic.com/api/oauth/usage` — the same
  endpoint Claude Code itself queries — as an `Authorization` header over HTTPS. It is never
  logged, written to disk, or sent anywhere else.
- `%USERPROFILE%\.claude\history.jsonl` is watched (via `FileSystemWatcher`) only to detect Claude
  Code activity; file contents are read only to build the local activity chart, never uploaded.
- When auto-update is enabled, the app polls `api.github.com` for releases of this repository.
  The OAuth token is never sent there.

The usage endpoint is undocumented and gated to the official client, so ClaudeBar identifies itself
with the CLI's User-Agent. It may change or stop working at any time.

## Disclaimer

ClaudeBar is an unofficial tool. It is not affiliated with, endorsed by, or supported by Anthropic.

## License

[MIT](LICENSE)
