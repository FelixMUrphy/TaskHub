# TaskHub (C# / .NET 8)

Windows tray notifier and mini control panel for long-running tasks. Any process (script, Claude, ChatGPT/Codex,
PowerShell, curl) registers a task over a local HTTP API (`API.md`, also `GET /api/help`), reports progress and
rich widgets, declares its own controls, and gets toasts + a panel. Port 47800, data in `%APPDATA%\TaskHub`.

## Features
- One native process `app\TaskHubHost.exe`: Kestrel HTTP server + WinForms tray + WebView2 panel + WinRT toasts.
  No Python, pystray, pywebview or PowerShell-per-toast.
- **Interactive toasts**: buttons for controls declared with `"toast": true` (with text box / combo box when the
  control has `input`), link buttons on finished tasks, "Відкрити панель" button. Clicks work even if the hub was
  closed (COM activation starts it).
- **Progress toasts** (opt-in): `"progress_toast": true` at registration (or `progress_toasts` in config.json) shows
  one toast with a live progress bar updated in place.
- **Panel**: frameless, resizable from edges, rounded corners + shadow (Win11), drag by header, hides on focus loss /
  Esc / tray click, remembers its size (`panel.json`).
- **CLI** `app\taskhub.exe` (no Python): `health, notify, list, show, register, update, log, finish, control,
  commands, ack, wait, panel, delete, api, run`. `taskhub run -- <cmd>` wraps any command and understands
  `::progress 5/20`, `::percent 40`, `::status ...`, `::stage ...`, `::warn ...`, `::error ...`, `::toast ...`,
  `::metric k=v`, `::widget id=value` lines printed by the command. `taskhub --help` for details.
- `/api/health` returns `"impl": "dotnet"`.
- `clients\python\taskhub_client.py` - stdlib Python client (`Task`, widgets/controls builders, `run` wrapper), `demo.py`.

## Folder layout
- `src\Host` - hub (TaskHubHost), `src\Cli` - CLI, `ui.html`, `API.md` - copied into `app` on build.
- `build.cmd` - `dotnet publish` both into `app\` (needs .NET 8 SDK; runtime needs .NET 8 Desktop + ASP.NET Core).
- Optional `app\TaskHubHost.settings.json` (`{"port": 47801, "data": "%APPDATA%\\TaskHub2"}`) - run a second,
  independent instance (testing). `taskhub.exe` next to it picks up the same port.

## Install
`build.cmd`, then `app\TaskHubHost.exe --install` (HKCU Run "TaskHub" + `taskhub://` protocol, no admin).
Remove with `--uninstall`.

## Hub command line
`TaskHubHost.exe [--port N] [--data DIR] [--install | --uninstall | --status | --panel [ID] | --no-tray]`
