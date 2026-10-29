# TaskHub API v1

Local hub for long-running tasks: tray icon + toasts + mini panel with live widgets and controls.
Base URL: `http://127.0.0.1:47800` (env `TASKHUB_URL`). Localhost only. JSON in/out.

**Rules for every client (scripts, Claude, ChatGPT/Codex, PowerShell, curl):**
- Write requests (`POST`/`DELETE`) MUST send header `X-TaskHub: 1` (CSRF guard). Browser requests from other origins are rejected.
- If `config.json` has `"token"`, send `Authorization: Bearer <token>` (env `TASKHUB_TOKEN` for the Python client).
- Be fail-soft: if the hub is not reachable (`GET /api/health` fails), keep working without it.
- Throttle updates to ≤ 2–4 per second. Chart points are appended per update, so batch them (see `chart`).
- Always `finish` the task (done/failed/cancelled). If you pass `pid`, the hub marks the task `lost` when the process dies without finishing.

## Quick start

**Any command, no code changes (native CLI `taskhub`, on PATH after install; full path `%LOCALAPPDATA%\Programs\TaskHub\taskhub.exe`):**
```
taskhub run --name "Video export" --source claude --auto-progress --heartbeat 600 -- ffmpeg -i in.mov -c:v libx265 out.mp4
```

**Python scripts (stdlib-only `taskhub_client.py`, installed next to the CLI in `%LOCALAPPDATA%\Programs\TaskHub`; copy it next to your script or add that folder to PYTHONPATH):**
```python
from taskhub_client import Task, W, C
with Task("Train YOLO", total=100, unit="ep", source="claude",
          widgets=[W.chart("loss", "Loss", series=["train", "val"]), W.gauge("gpu", "GPU", unit="%", warn=85)],
          controls=[C.signal("ckpt", "Save checkpoint")], pausable=True, milestones=[50]) as t:
    t.on("ckpt", lambda: save() or "saved")            # runs in background thread; return -> shown in panel
    for ep in range(100):
        t.wait_if_paused()
        if t.stop_requested: break                       # "Stop" button (added by default)
        t.progress(ep + 1, status=f"epoch {ep+1}", widgets={"loss": {"train": tl, "val": vl}, "gpu": 77})
        t.log(f"epoch {ep+1} done", toast=(ep % 25 == 24))  # toast = Windows notification
# exception -> failed (with traceback), Ctrl+C -> cancelled, stop -> cancelled, else done
```
Other helpers: `t.set(gpu=71, lic="ok")` (widget values), `t.metric(k=v)`, `t.status("…")`, `t.pending()` (commands without handler → `cmd.ack(ok, msg)`), `t.heartbeat()`, `t.done()/fail()/cancel()`, `notify(title, msg)`.

**Wrap any command with the Python client (equivalent of `taskhub run`):**
```
python taskhub_client.py run --name "Video export" --auto-progress --heartbeat 600 -- ffmpeg -i in.mov -c:v libx265 out.mp4
python taskhub_client.py run --name build --progress-regex "\[(?P<cur>\d+)/(?P<tot>\d+)\]" -- ninja
```
Streams output to the log (and echoes it), parses progress, adds Kill / Ctrl+C / "Send input" (stdin) controls, finishes with the exit code and returns it.

**Other CLI:** `notify "Title" "msg" --level warn` · `list` · `show ID` · `control ID CID --value V` ·
`register --name X [--pid N] [--json '{...}']` (prints id) · `update ID --percent 40 --status s --log l --toast --json '{...}'` ·
`finish ID --state done|failed|cancelled --message m` · `panel [ID]` · `health`

**PowerShell:**
```powershell
$h=@{"X-TaskHub"="1"}; $u="http://127.0.0.1:47800"
$id=(Invoke-RestMethod "$u/api/tasks" -Method Post -Headers $h -ContentType application/json -Body (@{name="Export";total=50;pid=$PID}|ConvertTo-Json -Depth 6)).id
Invoke-RestMethod "$u/api/tasks/$id/update" -Method Post -Headers $h -ContentType application/json -Body '{"current":10,"status":"file 10"}'
Invoke-RestMethod "$u/api/tasks/$id/finish" -Method Post -Headers $h -ContentType application/json -Body '{"state":"done","message":"ok"}'
```

**curl:** `curl -s -X POST localhost:47800/api/notify -H "X-TaskHub: 1" -d '{"title":"Done","message":"render finished"}'`

## Endpoints

| Method | Path | Body / query | Purpose |
|---|---|---|---|
| GET | `/api/health` | | `{ok, app:"TaskHub", version, active}` |
| GET | `/api/help` | | this document |
| GET | `/api/tasks` | `?since=V&wait=25` long-poll, `?active=1`, `?brief=1` | all tasks + `version` |
| POST | `/api/tasks` | Registration (below) | create / re-register → `{id}` |
| GET | `/api/tasks/{id}` | | full task |
| POST | `/api/tasks/{id}/update` | Update (below) | progress, status, widgets… |
| POST | `/api/tasks/{id}/log` | `{msg: str or [str], level?, toast?}` | append log |
| POST | `/api/tasks/{id}/heartbeat` | | liveness only |
| POST | `/api/tasks/{id}/finish` | `{state: done/failed/cancelled, message?, result?, widgets?}` | finish + toast |
| GET | `/api/tasks/{id}/commands` | `?wait=25` (long-poll, max 60) | pending signals for the process |
| POST | `/api/tasks/{id}/commands/{cmd}/ack` | `{ok: bool, message}` | reply, shown in panel log |
| POST | `/api/tasks/{id}/controls/{cid}` | `{value?}` | trigger a control (same as panel button) |
| DELETE | `/api/tasks/{id}` | `?force=1` for active | remove |
| POST | `/api/notify` | `{title, message, level: info/warn/error, task?}` | one-off toast |
| POST | `/api/hooks/claude` | Claude Code hook input (as sent) | `Stop` / `Notification` → toast; always `{}` (below) |
| POST | `/api/clear` | | remove finished tasks |
| POST | `/api/settings` | `{muted?: bool, seen?: true}` | mute toasts |
| POST | `/api/ui/open` | `{task?}` | open panel (focus a task) |

## Registration `POST /api/tasks`
```jsonc
{
  "name": "Parameter sweep 1–200",          // required
  "id": "sweep-a",                        // optional [A-Za-z0-9_.-]; same id = resume (log/charts kept)
  "source": "claude",                     // who started it: claude | chatgpt | codex | script ...
  "description": "…", "total": 200, "unit": "pts",
  "pid": 12345,                           // lost-detection + automatic "Kill" control ("kill_control": false to disable)
  "heartbeat_timeout": 600,               // s without update -> "stale" + toast (0 = off)
  "notify": {"start": false, "finish": true, "fail": true, "stale": true, "milestones": [25, 50, 75]},
  "cwd": "E:/proj", "links": [{"label": "results", "url": "file:///E:/proj/out"}], "meta": {},
  "controls": [],                         // see Controls
  "widgets":  [],                         // see Widgets
  "progress_toast": false                 // C# hub: one live Windows notification with a progress bar, updated in place
}
```

## Update `POST /api/tasks/{id}/update` (all fields optional)
`progress` 0..1 · `percent` 0..100 · `current` (+`total`) · `status` · `stage` · `log` (str or [str]) + `level` (info/warn/error) ·
`toast`: true or string → Windows notification now · `metrics`: `{k: v}` (null deletes; key-value list) ·
`widgets`: `{widget_id: value}` · `state`: running/paused · `add_widgets` / `remove_widgets` · `add_controls` / `remove_controls` / `controls` (replace) · `links` · `meta`.

## Widgets
Declared in `widgets[]` (or `add_widgets`), values via `update.widgets`. Common fields: `id`, `type`, `label`, `unit`,
`span` (1 = half width, 2 = full; chart/table/kv/progress default full), `color` (CSS), `decimals`.
Thresholds: `warn`, `crit` (value ≥ → amber/red; `invert: true` → value ≤ is bad).

| type | extra fields | value |
|---|---|---|
| `text` | `mono` | string |
| `progress` | `total` | fraction 0..1 (or 0..100), or count if `total`; also `{value, total, label}` |
| `gauge` | `min`, `max`, `warn`, `crit`, `invert` | number (half-circle) |
| `stat` | `warn`, `crit`, `invert` | number; shows ▲▼ delta vs previous |
| `led` | `text` | `"ok"`/`"warn"`/`"error"`/`"busy"`/`"off"`, bool, or `{state, text}` |
| `kv` | | object `{key: value}` |
| `table` | `columns` | list of rows (lists or dicts); replaces table |
| `chart` | `series: [names]`, `kind: line/area/bar`, `max_points` (120), `y_min`, `y_max`, `warn`, `crit`, `height` | **appends**: number · `{series: v}` · `[v1, v2]` · batch `[{"t": unix_ts, "S11": -12}, …]`; `null` clears |

```jsonc
{"widgets": [
  {"id": "s", "type": "chart", "label": "Loss", "series": ["train", "val"]},
  {"id": "gpu", "type": "gauge", "label": "GPU", "unit": "%", "warn": 80, "crit": 95},
  {"id": "lic", "type": "led", "label": "License"}
]}
// update:
{"current": 57, "widgets": {"s": {"train": 0.42, "val": 0.51}, "gpu": 73, "lic": "ok"}}
```

## Controls
Declared by the task, rendered as buttons. Fields: `id`, `label`, `kind`, `style` (default/primary/danger),
`confirm` (dialog text), `hint`, `states` (when shown; default running/paused/stale), `input` (value prompt),
`toast: true` (C# hub: also show this control as a button in the task's Windows notifications; `input` becomes a text/combo box there).

| kind | on click |
|---|---|
| `signal` | queued for the process → it receives via `GET /commands?wait=25` → replies with `ack` |
| `shell` | hub runs `command` (list or string; placeholders `{value}` `{pid}` `{task_id}`), output → task log; `cwd`, `timeout` |
| `open` | hub opens `target` (file, folder, URL) |
| `kill` | hub kills `pid` with its whole process tree; task → cancelled |

`input`: `{"type": "text"|"number"|"select"|"bool", "label", "default", "options": [...], "min", "max", "step"}`.
The value is sent only when the control's button is pressed, so give such controls an action label ("Set speed", "Apply", "Send"),
and `input.label` for the field itself. The hub remembers the last sent value (`last_value` in the control) and pre-fills the panel form
and the toast input with it.
Command delivered to the process: `{"id": "a1b2c3", "control": "lr", "value": 0.001, "t": 1793240199.6}`.
Python client built-ins: `stop` → `t.stop_requested`; `pause`/`resume` (`pausable=True`) → `t.paused`, `t.wait_if_paused()`.

## States
`running` · `paused` · `stale` (no update for `heartbeat_timeout`) · `done` · `failed` · `cancelled` · `lost` (pid died without finish).
Toasts: finish / fail / stale / milestones (per-task `notify`), `toast` in update/log, `/api/notify`. Global mute in tray or panel.

## Native CLI `taskhub` (no Python needed; starts the hub if it is not running, disable with `TASKHUB_NO_AUTOSTART=1`)
`taskhub run --name "<what>" --source <agent> [--auto-progress] [--heartbeat N] -- <cmd args>` wraps any command
(echo + log + progress; the command may print `::progress 5/20`, `::percent 40`, `::status s`, `::stage s`, `::warn s`,
`::error s`, `::toast s`, `::metric k=v`, `::widget id=value` lines). Also `notify`, `list`, `show`, `register`, `update`,
`log`, `finish`, `control`, `commands`, `ack`, `wait ID` (blocks until finished), `panel`, `delete`. `taskhub --help`.

## Claude Code hooks
Notifications when Claude Code finishes a response (`Stop`, toast shows the first line of the answer and the project folder)
or waits for a permission / your answer (`Notification`). Add to `~/.claude/settings.json` (or install the
[taskhub-claude-code](https://github.com/FelixMUrphy/taskhub-claude-code) plugin, which contains the same hooks):
```json
{
  "hooks": {
    "Stop": [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:47800/api/hooks/claude", "headers": { "X-TaskHub": "1" }, "timeout": 5 }] }],
    "Notification": [{ "matcher": "permission_prompt|elicitation_dialog",
      "hooks": [{ "type": "http", "url": "http://127.0.0.1:47800/api/hooks/claude", "headers": { "X-TaskHub": "1" }, "timeout": 5 }] }]
  }
}
```
The endpoint never returns a decision, so it cannot block or change Claude Code; if TaskHub is not running the hook just fails
silently (non-blocking). Toasts follow the usual rules (mute, no pop-ups while the panel is open).

## Agent notes (Claude / ChatGPT / Codex / any automation)
Use TaskHub for anything that runs longer than ~1 minute, so the user sees progress and gets notified without watching the chat.
0. Where: CLI `taskhub` (PATH) or `%LOCALAPPDATA%\Programs\TaskHub\taskhub.exe`; HTTP `http://127.0.0.1:47800`; this doc: `taskhub api` or `GET /api/help`.
   If the CLI is not on PATH in your shell, call it by the full path. From WSL/containers use the Windows host address only if the user allows it (the hub listens on loopback).
1. `taskhub health` (or `GET /api/health`) first. If the hub is down and cannot be started, work normally - never fail the user's task because of TaskHub.
2. Long shell command: `taskhub run --name "<what>" --source <claude|chatgpt|codex> [--auto-progress] [--heartbeat N] -- <cmd args>`.
   Inside your own scripts print `::progress i/N`, `::status ...`, `::warn ...` lines - `taskhub run` turns them into panel updates.
   `taskhub wait <id>` blocks until a task finishes (exit 0 = done).
2a. Name things for the user: task `name` = what is being done ("Parameter sweep 1-200"), `source` = who started it.
    Controls with `input` send their value only when the button is pressed, so label them as actions ("Set speed", "Apply").
    Use `"toast": true` only for 1-3 controls the user may need from a notification (Pause, Stop, a key parameter).
3. For your own Python scripts use `Task(...)` with meaningful widgets; `toast=True` only for events the user must see.
4. Before saying a task is still running, check `GET /api/tasks?active=1&brief=1`.
5. To act on a task: read its `controls`, then `POST /api/tasks/{id}/controls/{cid}` with `{value}` if it has `input`.
