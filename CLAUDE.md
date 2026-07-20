# ClaudeBar (Windows) — AI instructions

Native Windows port of the macOS ClaudeBar. Stack: C# / .NET 8 / WPF, with a WinForms
`NotifyIcon` for the system tray. Single project at `src/ClaudeBar/`.

## After any code change

Always rebuild and restart the app immediately:

```powershell
./run.ps1
```

This kills the running instance, recompiles (Debug), and relaunches it in the system tray. Do not
skip this step — the tray app must be running to verify that a change works.

## Layout

- `Models/` — pure data + forecasting logic (`UsageModel`, `ActivityProfile`, `UsageSample`).
- `Services/` — platform integration and IO (`CredentialReader`, `DesktopTokenReader`, `OAuthApi`,
  `ActivityWatcher`, `ActivityHistory`, `UsageHistoryStore`, `Recommender`, `LaunchAtLogin`,
  `NotificationManager`, `AutoUpdater`, `AppPaths`, `Settings`).
- `ViewModels/UsageStore.cs` — the state machine (timer, rate-limit backoff, token fallback).
- `Views/` — `TrayIconManager` (tray + dynamic icon), `MenuWindow` (popover), `StatisticsWindow`
  (hand-drawn charts), `UiTheme`.

## Gotchas

- WinForms global usings (`System.Drawing`, `System.Windows.Forms`) are removed in the `.csproj`
  so WPF's `Brush`/`Color`/`Rectangle`/`Application` are unambiguous. `TrayIconManager` imports
  those two namespaces explicitly — it's the only file that needs GDI+/WinForms.
- Self-contained single-file packaging is applied **only at publish** (`publish.ps1`), never in the
  plain build — the single-file apphost self-extracts/relaunches, which breaks `dotnet build` runs.
- Background callbacks (FileSystemWatcher, retry tasks) must marshal to the UI via the captured
  `_dispatcher` in `UsageStore`, not `Dispatcher.CurrentDispatcher`.
- Prefer PowerShell for build/run here (the real host). The Bash tool runs in a sandbox with a
  different filesystem view.

## Data locations (Windows)

- CLI token: `%USERPROFILE%\.claude\.credentials.json` (plaintext JSON, `claudeAiOauth.accessToken`)
- Desktop token cache: `%APPDATA%\Claude\config.json` + `Local State` (os_crypt v10 / DPAPI)
- Activity: `%USERPROFILE%\.claude\history.jsonl`
- Our data + settings: `%APPDATA%\ClaudeBar\` (`usage-history.jsonl`, `settings.json`, `error.log`)
