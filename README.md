# TaskHub

Windows tray notifier and mini control panel for long-running tasks. Any process (script, Claude, ChatGPT/Codex,
PowerShell, curl) registers a task over a local HTTP API, reports progress and rich widgets, declares its own controls,
and gets Windows notifications plus a pop-up panel. Port 47800 (loopback only), data in `%APPDATA%\TaskHub`.

## Install (per user, no admin, no .NET needed)
1. Unzip `TaskHub-<version>-win-x64.zip` anywhere and run `TaskHubHost.exe`.
2. Answer **Yes** to "Install TaskHub for the current user?". It installs to `%LOCALAPPDATA%\Programs\TaskHub`, starts with
   Windows, adds the `taskhub` command to the user PATH, registers `taskhub://` and an entry in *Apps & features*.

Silent: `TaskHubHost.exe --install --quiet`. Uninstall: *Apps & features* → TaskHub, or
`TaskHubHost.exe --uninstall [--quiet] [--purge]` (`--purge` also deletes `%APPDATA%\TaskHub`).

Autostart is self-healing: the installed hub restores its Run entry (and re-enables it if it was disabled in Task
Manager) on every start, unless you switched autostart off in the tray menu. `taskhub` (CLI) starts the hub if it is not
running. Requirements: Windows 10 1809+ / 11, WebView2 runtime (preinstalled on current Windows).

## Use
- Tray icon: left click = panel, right click = menu (mute, clear finished, autostart, language, data folder, API, exit).
- Panel: frameless, resizable, drag by the header, hides on focus loss / Esc. Language: 🌐 selector (10 languages + auto).
- Notifications: buttons for controls marked `"toast": true` (with input/combo boxes), "Open panel"; optional live
  progress notification (`"progress_toast": true`). No pop-ups while the panel is open; on-screen time 4 s / 8 s
  (`toast_seconds`, `toast_seconds_interactive` in `config.json`), afterwards they stay in the notification center.
- API: `API.md` (also `taskhub api` / `GET /api/help`). Agents: see "Agent notes" there.
- CLI: `taskhub --help` - `health, notify, list, show, register, update, log, finish, control, commands, ack, wait,
  panel, delete, api, run`. `taskhub run --name X -- <cmd>` wraps any command (progress parsing, `::directives`).
- Python: `taskhub_client.py` (stdlib only, installed next to the CLI): `Task`, widget/control builders, `run` wrapper.

## Build
`build.cmd` → `app\` (self-contained win-x64) and `dist\TaskHub-<version>-win-x64.zip`; `build.cmd nozip` skips the zip.
Needs the .NET 8 SDK. Sources: `src\Host` (hub: Kestrel HTTP API, WinForms tray, WebView2 panel, WinRT toasts),
`src\Cli` (CLI), `ui.html` (panel), `clients\python`.

## Tests
`tests\run_tests.cmd` starts an isolated hub (port 47899, temp data, no tray), runs `api_test.py` (API contract, 45 checks)
and `parallel_test.py` (8 concurrent workers + notification burst), then stops it. `tests\demo_long.py` is an interactive
~20 min demo against the main hub (all widgets, panel and notification controls).

## Hub command line
`TaskHubHost.exe [--port N] [--data DIR] [--install|--uninstall [--quiet] [--purge] | --status | --panel [ID] | --no-tray]`.
An optional `TaskHubHost.settings.json` next to the exe (`{"port": 47801, "data": "%APPDATA%\\TaskHub2"}`) runs an
independent second instance (development); `taskhub.exe` next to it uses the same port.
