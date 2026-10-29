# TaskHub

**One place to watch and control everything that runs in the background on your Windows PC** — training runs,
simulations, renders, builds, backups, and the scripts your AI agents (Claude, Codex, ChatGPT) start for you.

Any process reports progress over a tiny local HTTP API and gets a tray icon, Windows notifications and a pop-up panel
with live charts, gauges and **its own buttons** (pause, change a parameter, save a snapshot, kill). No cloud, no account,
loopback only.

<p align="center">
  <img src="docs/panel-dark.png" width="420" alt="TaskHub panel (dark)">
  <img src="docs/panel-light.png" width="420" alt="TaskHub panel (light)">
</p>

[**⬇ Download the latest setup.exe**](https://github.com/FelixMUrphy/TaskHub/releases/latest) ·
[API reference](API.md) · [❤ Sponsor](https://github.com/sponsors/FelixMUrphy)

## Why
- You start a 6-hour job and want to know when it finishes, fails or hangs — without watching a console.
- An AI agent launches long scripts for you; TaskHub gives them one documented way to report status and to receive your
  commands back ("lower the learning rate", "stop after this epoch").
- Several things run in parallel and you want one glance at all of them.

## Features
- **Tray icon** with a progress ring and the number of active tasks; colour shows running / stalled / failed / done.
- **Panel** (click the tray icon): every task with progress, ETA, status, log and widgets — line/area/bar charts, gauges,
  stats with deltas, LEDs, key-value lists, tables, text.
- **Controls declared by the task itself**: buttons, number/text/select inputs, confirmations, shell commands,
  open-file/folder, graceful stop, pause/resume, kill. Values go back to the process over long-polling.
- **Windows notifications** for start/finish/failure, milestones and log events, optionally with the task's buttons and
  input fields; a live progress notification; quiet while the panel is open.
- **Stall detection** (heartbeat timeout) and *lost* state when the process dies.
- **Agent-friendly**: a native `taskhub` CLI on PATH, a stdlib-only Python client, plain HTTP for everything else;
  `taskhub api` prints the docs an agent needs.
- 10 UI languages (English, Українська, Español, Deutsch, Français, Italiano, Português, Polski, 中文, 日本語).
- Per-user install, no admin rights, no .NET runtime needed; self-healing autostart.

## Quick start
1. Run `TaskHub-<version>-setup.exe` → **Install** (choose a folder, autostart, PATH).
2. Report something:

```powershell
taskhub notify "Render" "finished in 42 min"
taskhub run --name "Build" -- cmake --build build -j 16     # wraps any command, parses % from its output
```

```python
from taskhub_client import Task, W, C          # stdlib only; installed next to taskhub.exe

with Task("Train", total=100, unit="epochs", source="codex",
          widgets=[W.chart("loss", "Loss", series=["train", "val"]), W.gauge("vram", "VRAM", max=24, unit="GB")],
          controls=[C.signal("lr", "Set LR", input=C.number(default=1e-3))], pausable=True) as t:
    t.on("lr", lambda v: set_lr(v))
    for e in range(100):
        t.wait_if_paused()
        if t.stop_requested: break
        t.progress(e + 1, status=f"epoch {e+1}", widgets={"loss": {"train": tl, "val": vl}, "vram": mem})
```

```bash
curl -s -X POST localhost:47800/api/notify -H "X-TaskHub: 1" -d '{"title":"Done","message":"backup ok"}'
```

**With AI agents** — add one line to your `CLAUDE.md` / `AGENTS.md` / custom instructions:

> For any job longer than a minute, report it to TaskHub (`taskhub api` prints the docs; wrap commands with
> `taskhub run --name ... -- <cmd>`).

**Claude Code notifications** — get a toast when Claude finishes a response or needs your permission: install the
[taskhub-claude-code](https://github.com/FelixMUrphy/taskhub-claude-code) plugin or add the two hooks from
[API.md → Claude Code hooks](API.md#claude-code-hooks) to `~/.claude/settings.json`.

## Install details
The setup dialog installs to `%LOCALAPPDATA%\Programs\TaskHub` by default (or any folder you choose), unpacks the
`taskhub` CLI, `taskhub_client.py` and `API.md`, registers `taskhub://` and an *Apps & features* entry. Running a newer
setup over an existing install offers **Update**. *Run without installing* is also available.

- Silent: `TaskHub-<version>-setup.exe --install [--dir PATH] [--no-autostart] [--no-path] --quiet`
- Uninstall: *Apps & features* → TaskHub, or `TaskHubHost.exe --uninstall [--quiet] [--purge]`
  (`--purge` also deletes the data in `%APPDATA%\TaskHub`).
- Autostart restores itself (also if disabled in Task Manager) unless you turn it off in the tray menu.
- Requirements: Windows 10 1809+ / 11 x64, WebView2 runtime (preinstalled on current Windows).
- Port 47800, `127.0.0.1` only, CSRF header + Host/Origin checks; optional bearer token (`"token"` in `config.json`).

Toast timing: `toast_seconds` / `toast_seconds_interactive` (4 s / 8 s) in `config.json`; afterwards notifications stay
in the notification center.

## Build from source
`build.cmd` (needs the .NET 8 SDK) → `dist\TaskHub-<version>-setup.exe` (single self-contained file; the CLI is built
first and embedded) and `app\` for development. Sources: `src\Host` (Kestrel HTTP API, WinForms tray, WebView2 panel,
WinRT toasts), `src\Cli`, `ui.html`, `clients\python`.

Tests: `tests\run_tests.cmd` starts an isolated hub (port 47899, temp data, no tray, no notifications) and runs
`api_test.py` (API contract) and `parallel_test.py` (8 concurrent workers + notification burst).
`tests\demo_long.py` is an interactive ~20 min demo against the main hub.

Hub command line: `TaskHubHost.exe [--port N] [--data DIR] [--install|--uninstall [--quiet] [--purge] | --status |
--panel [ID] | --no-tray]`. A `TaskHubHost.settings.json` next to the exe (`{"port": 47801, "data": "..."}`) runs an
independent second instance.

## Support
TaskHub is free. If it saves you time, [sponsoring](https://github.com/sponsors/FelixMUrphy) keeps it maintained —
and tells me which ideas are worth building next. Bug reports and feature requests: [Issues](https://github.com/FelixMUrphy/TaskHub/issues).

## License
[PolyForm Shield 1.0.0](LICENSE.md) — source-available: free to use, modify and share for any purpose, including at
work, **except** to provide a product that competes with TaskHub (e.g. repackaging or reselling it).
