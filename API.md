# TaskHub API v1

Local hub for long-running tasks: tray icon + toasts + mini panel with live widgets and controls.
Base URL: `http://127.0.0.1:47800` (env `TASKHUB_URL`). Localhost only. JSON in/out.

## AI agents: read this first
- **Claude Code, Codex and other MCP clients: use the TaskHub MCP tools** (`task_start`, `task_update`, `task_finish`,
  `task_commands`, `task_list`, `notify`). They need no HTTP, no headers and pass any language correctly.
- **You are Claude Code or Codex but don't see these tools?** Don't fall back to `curl` / HTTP. Tell the user, in one short
  message: *"Enable TaskHub tools (MCP): tray icon → AI agents → <Claude Code / Codex> — TaskHub tools (MCP), then start a new chat / session -
  MCP tools only appear in sessions started after that."* (Not installed yet: the setup dialog has the same option.)
  Meanwhile continue the user's actual work without TaskHub.
- Only if the user explicitly wants TaskHub in this session anyway, or your client has no MCP (ChatGPT, plain scripts): use
  the **`taskhub` CLI** or the Python client below - both send UTF-8.
- **Never put non-ASCII text (Cyrillic, CJK, emoji, accents) into `curl -d '...'` or a PowerShell 5.1 `-Body '...'` string on
  Windows**: they convert it to the ANSI code page and the user sees `????`. If you must use raw HTTP, send UTF-8 bytes:
  `curl --data-binary @body.json` (file saved as UTF-8) or `-Body ([Text.Encoding]::UTF8.GetBytes($json))`.

**Rules for every client (scripts, Claude, ChatGPT/Codex, PowerShell, curl):**
- Write requests (`POST`/`DELETE`) MUST send header `X-TaskHub: 1` (CSRF guard). Browser requests from other origins are rejected.
- If `config.json` has `"token"`, send `Authorization: Bearer <token>` (env `TASKHUB_TOKEN` for the Python client; the
  `taskhub` CLI and MCP server also read it from the hub's `config.json`). The token keeps out browsers and other users'
  sessions; processes of your own account can read `config.json` (and the panel page) anyway.
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
          controls=[C.signal("ckpt", "Save checkpoint")], pausable=True) as t:
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
$id=(Invoke-RestMethod "$u/api/tasks" -Method Post -Headers $h -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes((@{name="Export";total=50;pid=$PID}|ConvertTo-Json -Depth 6)))).id
Invoke-RestMethod "$u/api/tasks/$id/update" -Method Post -Headers $h -ContentType application/json -Body '{"current":10,"status":"file 10"}'
Invoke-RestMethod "$u/api/tasks/$id/finish" -Method Post -Headers $h -ContentType application/json -Body '{"state":"done","message":"ok"}'
```

**curl:** `curl -s -X POST localhost:47800/api/notify -H "X-TaskHub: 1" -d '{"title":"Done","message":"render finished"}'`
(ASCII only; for other languages `--data-binary @body.json` with a UTF-8 file - see [AI agents](#ai-agents-read-this-first)).

## Endpoints

| Method | Path | Body / query | Purpose |
|---|---|---|---|
| GET | `/api/health` | | `{ok, app:"TaskHub", version, edition: "exe"\|"store", active, notifications, toast_queue}` — `notifications` (2.9): Windows' switch for TaskHub's notifications, `enabled` \| `disabled_app` \| `disabled_user` \| `disabled_policy` \| `disabled_manifest` \| `unknown` (when disabled, toasts vanish silently; failure notifications fall back to a tray balloon); `toast_queue` (2.9): notifications waiting for the rate limit (`toast_rate` per minute, one per task every `toast_task_gap` s; bursts fold into the latest) |
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
| POST | `/api/hooks/claude`, `/api/hooks/codex` | agent hook input (as sent) | `Stop` / `Notification` / `PermissionRequest` → toast; always `{}` (below) |
| GET | `/api/telegram` | | Telegram bridge state `{configured, linked, bot, status, away_only, progress, link, storage, warning}` — `storage` (2.9): where the bot token is kept, `vault` (Windows Credential Manager) \| `config` (`config.json`, only when the Credential Manager is unavailable) \| null; `warning`: text for Settings, or null |
| POST | `/api/telegram` | `{token}` (start pairing, returns the t.me `link`) · `{test: true}` · `{relink: true}` (new code for another chat, same bot) · `{away_only: bool}` · `{progress: bool}` (live progress bar + charts at the end) · `{disconnect: true}` | Telegram setup |
| GET | `/api/connectors`, `/api/connectors/github` | | connector state: `{enabled, repos, interval, notify_success, logs, auth, token_set, token_check, token_check_text, login, token_expires, status, repo_status, rate_remaining, active_runs}` (the token itself is never returned) |
| GET | `/api/connectors?view=settings` | | the rows of the settings page's Connectors section (2.9): `{connectors: [{id, name, kind, enabled, uses_key, key_set, key_check, summary, help_url, thresholds?, setup?}], lang}` - `key_check` = the last verdict on the saved key (`ok` / `revoked` / `network` / `unavailable` / null), `summary` = the state line in the UI language ("Connected, balance $X", "Connected as @login · 3 repositories"...), `help_url` = the "how to get a key" page (`docs/keys/<id>.md`, or `<id>.<lang>.md` when that language exists); never a key |
| POST | `/api/connectors/github` | `{enabled?, repos?: ["owner/repo"], interval?: 15-3600, notify_success?, logs?, auth?: "token"\|"gcm", token?, forget_token?}` | GitHub Actions connector (below) |
| GET | `/api/connectors/runpod` | | `{enabled, key_set, key_check, key_check_text, status, can_act, probed_at, machines: [{id, name, state, task}], running, balance: {amount, spend_per_hr, spend_limit, runway_hours, raw, at}, low, idle_minutes, idle_util, low_runway_hours, low_balance, graphql_balance, summary}` (2.9; the key is never returned) |
| POST | `/api/connectors/runpod` | `{enabled?, key?, forget_key?, idle_minutes?: 1-1440, idle_util?: 1-50, low_runway_hours?: 0-720, low_balance?: $\|null, list_interval?: 30-3600, detail_interval?: 15-3600, billing_interval?: 300-86400, graphql_balance?}` | Runpod connector (2.9, below) |
| GET | `/api/connectors/vastai` | | the same as Runpod without `graphql_balance`; `balance.label` says what `amount` is ("Balance $x · Credit"), `balance.raw = {balance, credit, ...}` (2.9; the key is never returned) |
| POST | `/api/connectors/vastai` | `{enabled?, key?, forget_key?, idle_minutes?, idle_util?, low_runway_hours?, low_balance?, list_interval?, detail_interval?, billing_interval?}` (ranges as Runpod) | Vast.ai connector (2.9, below) |
| POST | `/api/connectors/runpod`, `/api/connectors/vastai` | `{guard?: bool, guard_warn?: $\|null, guard_stop?: $\|null, guard_grace_min?: 1-120, guard_snooze_min?: 5-240, guard_exempt?: [machine id]}` | balance guard (2.9, [below](#balance-guard)); `guard_warn` must be above `guard_stop` (>= 0), else 400 and nothing changes |
| GET | `/api/guard/log` | `?provider=runpod\|vastai&limit=1-200` (50) | what the balance guard stopped, newest first (2.9): `{entries: [{t, provider, machine_id, name, trigger: "auto"\|"stop_now", balance, threshold, burn_per_hr, spent, spent_estimated, result: "stopped"\|"failed", error, storage_per_day, storage_note, storage_approx}]}` |
| POST | `/api/clear` | | remove finished tasks |
| POST | `/api/settings` | `{muted?: bool, seen?: true | [task ids], language?, theme?: "system"\|"light"\|"dark", accent?: "system"\|"taskhub", panel_transparency?: 0-100, card_transparency?: 0-100, panel_group?: "project"\|"source"\|"none", panel_pinned?: bool}` | mute toasts; panel look (also in `GET /api/tasks` → `panel`) |
| POST | `/api/ui/open` | `{task?}` | open panel (focus a task) |

## Registration `POST /api/tasks`
```jsonc
{
  "name": "Parameter sweep 1–200",          // required
  "id": "sweep-a",                        // optional [A-Za-z0-9_.-]; same id = resume (log/charts kept)
  "source": "claude",                     // who started it: claude | chatgpt | codex | script ...
  "description": "…", "total": 200, "unit": "pts",
  "project": "shop-api",                  // panel groups (default: the folder name of "cwd")
  "attention": "Approve the deploy",      // the task waits for the user: shown on top of the panel ("" = no)
  "pid": 12345,                           // a Windows pid: lost-detection + automatic "Kill" control ("kill_control": false to disable);
                                          // 2.9: no Kill when the hub can't read the process's start time (not a Windows pid)
  "heartbeat_timeout": 600,               // s without update -> "stale" + toast (0 = off)
  "expected_duration": 120,               // s it usually takes: the ETA comes from it (not from the pace) until overdue
  "notify": {"start": false, "finish": true, "fail": true, "stale": true, "milestones": []},
                                          // milestones: e.g. [50] -> one toast at 50 % - only if the user asked for it;
                                          // milestone toasts are plain, "milestone_controls": true adds the task's toast controls
  "cwd": "E:/proj", "links": [{"label": "results", "url": "file:///E:/proj/out"}], "meta": {},
  "controls": [],                         // see Controls
  "widgets":  [],                         // see Widgets
  "progress_toast": false                 // C# hub: one live Windows notification with a progress bar, updated in place
}
```

## Update `POST /api/tasks/{id}/update` (all fields optional)
`progress` 0..1 · `percent` 0..100 · `current` (+`total`) · `status` · `stage` · `log` (str or [str]) + `level` (info/warn/error) ·
`toast`: true or string → Windows notification now · `metrics`: `{k: v}` (null deletes; key-value list) ·
`widgets`: `{widget_id: value}` · `state`: running/paused · `project` · `attention` (string; `""` clears, also cleared on finish) · `add_widgets` / `remove_widgets` · `add_controls` / `remove_controls` / `controls` (replace) · `links` · `meta` · `expected_duration`.
`links` (register and update): a list of URLs or `{"label", "url"}`. 2.9: only `http`, `https`, `mailto` and `file` URLs or an absolute
path (`E:/proj/out`, `\\server\share\x`) - anything else is rejected with 400. The panel opens web links in the browser and shows
a file or folder link in Explorer (a folder opens, a file is selected in its folder) - it never runs a linked program.
An update with an invalid entry in `controls` / `add_controls` / `add_widgets` / `links` is rejected as a whole (400, nothing changed). Numbers must be finite:
`NaN` / `Infinity` (also as strings) are ignored.

## Widgets
Declared in `widgets[]` (or `add_widgets`), values via `update.widgets`. Common fields: `id`, `type`, `label`, `unit`,
`span` (1 = half width, 2 = full; chart/table/kv/progress default full), `color`, `decimals` (0–10).
`color`: `#rgb` / `#rrggbb(aa)`, a colour name, `var(--name)` or `rgb()` / `rgba()` / `hsl()` / `hsla()` - anything else is
dropped. Numeric fields take numbers (numeric strings are converted, other values dropped); `label` is cut to 80 characters,
`unit` to 16, `text` to 1000.
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
| `chart` | `series: [names]`, `kind: line/area/bar`, `max_points` (120), `y_min`, `y_max`, `warn`, `crit`, `height` (px, 20–400) | **appends**: number · `{series: v}` · `[v1, v2]` · batch `[{"t": unix_ts, "train": 0.4}, …]`; `null` clears |

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
`confirm` (question asked before the control runs - a dialog in the panel, a Yes / No notification for toast buttons, a Yes button
in Telegram), `hint`, `states` (when shown; default running/paused/stale), `input` (value prompt),
`toast: true` (C# hub: also show this control as a button in the task's Windows notifications; `input` becomes a text/combo box there).

| kind | on click |
|---|---|
| `signal` | queued for the process → it receives via `GET /commands?wait=25` → replies with `ack` |
| `shell` | hub runs `command` (list or string; placeholders `{value}` `{pid}` `{task_id}`; see *Shell values* below), the last 25 output lines and the exit code → task log; `cwd`, `timeout` (s, default 300, 1–86400) |
| `open` | hub opens `target` (file, folder, URL) |
| `connector` | only on tasks of a built-in connector (not accepted from API clients): the connector does it (e.g. cancel / re-run on GitHub); `panel_only` ones (delete a Runpod pod / Vast.ai instance) answer 403 over HTTP - only the panel's own page can trigger them (2.9) |
| `kill` | hub kills `pid` with its whole process tree (only if it is still the process the task registered, never a reused pid); task → cancelled. 2.9: refused (409) when the process's start time couldn't be read at registration; if the process is still running after the kill (e.g. it runs elevated: access denied) → 500 with the reason and the task stays as it was (before 2.9: marked cancelled anyway) |

`input`: `{"type": "text"|"number"|"select"|"bool", "label", "default", "options": [...], "min", "max", "step"}`.
Values are checked by the hub for every surface (400 otherwise): a number within `min`/`max`, `true`/`false` for bool, one of
`options` for select, text as a string (cut to 2000 characters).
The value is sent only when the control's button is pressed, so give such controls an action label ("Set speed", "Apply", "Send"),
and `input.label` for the field itself. The hub remembers the last sent value (`last_value` in the control) and pre-fills the panel form
and the toast input with it.
**Shell values** (2.9). The value (from the panel, a notification or Telegram) is never pasted into a command line as text:
- every form: the hub also passes it in the environment variable `TASKHUB_VALUE` (absent when the value is empty);
- string `command` (run by `cmd.exe /d /s /c`): `{value}` becomes `!TASKHUB_VALUE!` and cmd runs with delayed expansion (`/v:on`),
  which expands it only after the line is split into commands, pipes and redirections - `& | < > ^ % " !` in the value stay plain
  text (an empty value becomes nothing). Because of `/v:on`, `!` and `^` written in such a template itself are special to cmd (write `^!` for a literal `!`). The value still
  reaches the program as words: a value with spaces is several arguments unless the template quotes it (`"{value}"`) - for exact
  arguments use the list form;
- list `command`: each item is one argument (quoted for the program). `{value}` can't be in the first item (the program), and a
  `.bat` / `.cmd` program can't take `{value}` in a list (cmd.exe would parse it) - both rejected with 400.

Command delivered to the process: `{"id": "a1b2c3", "control": "lr", "value": 0.001, "t": 1793240199.6}`.
Python client built-ins: `stop` → `t.stop_requested`; `pause`/`resume` (`pausable=True`) → `t.paused`, `t.wait_if_paused()`.

## States
`running` · `paused` · `stale` (no update for `heartbeat_timeout`) · `done` · `failed` · `cancelled` · `lost` (pid died without finish).
Toasts: finish / fail / stale / milestones (per-task `notify`), `toast` in update/log, `/api/notify`. Global mute in tray or panel.

## Connectors: GitHub Actions
Panel → ⚙ Settings → Connectors → GitHub Actions (or `POST /api/connectors/github`). Each workflow run of the chosen repositories is a task:
id `gh-<run id>`, source `github`, project = repository name, description `owner/repo · branch · sha · @actor · event`;
progress by the steps of all jobs, `stage` = job › step, ETA from the median duration of the workflow's last successful runs;
on failure the message names the job › step, and (option `logs`) the last 30 lines of its log go to the task log.
Buttons: open on GitHub, cancel (running), re-run failed jobs (failed), re-run all (finished), each with a confirmation; links to
the artifacts and to the release of a tag after a successful run. Failures always notify (Windows + Telegram), successes if
`notify_success`. Runs that finished before TaskHub started are not shown; a re-run (also one started on github.com) shows the task again.
Access: your own fine-grained token (Actions: Read and write, Contents: Read-only, Metadata: Read-only; only these repositories),
kept in the Windows Credential Manager - or `auth: "gcm"`, the Git Credential Manager's github.com token (broad rights).
Requests go only to api.github.com (with the token) and, for logs, to GitHub's log storage through the one-time link GitHub gives
(without the token). Polling uses ETags (unchanged = free): every `interval` s (default 60) when idle, every 10 s while a run is
going, slower near the rate limit. A notification warns 7 days before the token expires.

## Connectors: Runpod
*2.9.* Panel → ⚙ Settings → Connectors → Runpod (or `POST /api/connectors/runpod {key, enabled: true}`). The key is checked with
Runpod, then kept in the Windows Credential Manager (never `config.json`, never returned): a key Runpod rejects (401 / 403) -> 400,
nothing saved; when Runpod can't be asked (no network, 5xx, 429) the key is saved with `key_check` `network` / `unavailable`
and checked again by the polling; a working one -> `key_check: "ok"` and the balance at once (*Connected, balance $X*).
`enabled: false` keeps the key; only `forget_key` deletes it (the same for Vast.ai, and GitHub's `token` / `forget_token`). Each pod is a task: id `rp-<pod id>`,
source `runpod`, project `Runpod`, `meta.runpod = {id, name, state, status}`. RUNNING / PROVISIONING / STARTING → active
(status *Starting…* until it runs); EXITED → `done` "stopped"; ERROR → `failed`; deleted / gone from the list → `done`
"terminated"; started again → the same task is active again. Widgets: `info` (kv: GPU, region, image, price, spent, uptime,
SSH, balance), `gpu` (chart: GPU = highest util of its GPUs, VRAM) and `load` (chart: CPU, RAM), 240 points, from TaskHub's own
polls (Runpod keeps no history). Links: `https://<pod>-<port>.proxy.runpod.net` for each `<port>/http`.
Buttons (only when the key may run actions - probed with `POST /v2/pods/pod_taskhub_probe/action` when the key changes and
daily: 403 = read-only): stop / restart (running; also in toasts and Telegram), start (stopped), delete (panel only: sent as a WebView2
message after typing the pod's exact name; HTTP, toasts and Telegram get 403) - each with a confirmation. Errors: 409 *not possible in the current
state*, *no free GPU* for a start whose host is full, 403 *the key may not do this*.
GPU idle: every GPU under `idle_util` % (5) for `idle_minutes` (30) → `attention` + a warning (Windows + Telegram, with Stop),
once per idle period. Balance: GraphQL `myself {clientBalance currentSpendPerHr spendLimit}` (`graphql_balance`, default on);
runway = balance / spend per hour (Runpod's, else the sum of the running pods' prices); runway under `low_runway_hours` (12)
or balance under `low_balance` → one warning per crossing + `attention` on running pods. Spent = pod billing of the last 30
days (≈). Polling: pod list every 60 s (`list_interval`, all pages), each running pod every 30 s (`detail_interval`), billing
and balance every 10 min (`billing_interval`); Runpod's `RateLimit` header and 429 `Retry-After` pause everything.

## Connectors: Vast.ai
*2.9.* Panel → ⚙ Settings → Connectors → Vast.ai (or `POST /api/connectors/vastai {key, enabled: true}`); the key is kept like
Runpod's. Each instance is a task: id `va-<instance id>`, source `vastai`, project `Vast.ai`, name = its label (else
"instance <id>"), `meta.vastai = {id, name, state, status}`. `actual_status` running → active; `intended_status` running while
not yet running (loading, scheduling...) → active, *Starting…* (after a minute in *Scheduling*: "waiting for its GPU"); asked to
stop / exited → `done` "stopped"; offline → `failed`; destroyed / gone from the list → `done` "terminated". Widgets as Runpod
plus a disk series in `load` (CPU, RAM, Disk); load values Vast.ai leaves null are null points. Uptime from `uptime_mins`, else
`start_date`. Link: the Vast.ai console. Buttons (probe: `PUT /api/v0/instances/0/ {label}` - 403 = read-only): stop / reboot
(running), start (stopped), delete = destroy (panel only, typed name) - as Runpod. Vast.ai's `200 {"success": false, msg}`
answers count as errors (its `msg` is shown). Spent = `/api/v0/charges` of the last 30 days (≈); balance and credit from
`/api/v0/users/current` (credit, when present, is `amount`). Polling as Runpod (list paged 25 at a time); a 429 (Vast.ai sends
no Retry-After) pauses everything 60 s, doubling while 429s follow each other (up to 30 min).

## Balance guard
*2.9, off by default.* Per machine connector (`connectors.runpod`, `connectors.vastai` in `config.json`): `guard` (false),
`guard_warn` ($, default 10), `guard_stop` ($, default 2; `guard_warn` > `guard_stop` >= 0), `guard_grace_min` (10, 1-120),
`guard_snooze_min` (15, 5-240), `guard_exempt` (machine ids that are never stopped by the guard). `low_runway_hours` /
`low_balance` keep their meaning and stay in the panel; while the guard is on, their warning is skipped only for a reading
the guard's own warning covers (balance ≤ `guard_warn`), so the two never fire for the same crossing.
Burn = Runpod's `currentSpendPerHr`, else the sum of the running machines' $/h (Vast.ai: `dph_total`); hours to stop =
(balance − stop) / burn. `GET /api/connectors/<id>` → `guard: {enabled, warn, stop, grace_min, snooze_min, exempt, can_act,
balance_source, burn_per_hr, hours_to_stop, stop_at, zero_at, unreadable, warned, stops, no_stop, retry: [machine id],
pending: {seq, deadline, held, capped, machines, task}|null}`; the settings rows (`?view=settings`) carry `guard` with the texts in the UI language (forecast,
pending line, machines with `exempt`, the last log lines, a note when it can only warn).
- balance ≤ warn: one warning per crossing (re-armed when the balance is back above warn + 5 %) with balance, burn, hours.
- balance ≤ stop: a pending stop of the running, non-exempt machines, shown as the task `<prefix>--guard` (`rp--guard`,
  `va--guard`; source = the connector) with the buttons `gstop.<seq>` (Stop now, asks for confirmation first — panel,
  toast, Telegram) and `gsnooze.<seq>` (Snooze), also in the notification and Telegram. Deadline = now + grace, capped at the projected $0 minus 5 min; Snooze adds `guard_snooze_min`
  (same cap; the reply says until when). A press on a button of an earlier pending stop answers "already handled". A balance
  above stop cancels it. At the deadline the balance is read again: unreadable → nothing is stopped, `held`, retried each
  minute; *Stop now* still acts. Stop = the connector's stop action (never delete), confirmed by polling the list; one result
  per machine in `guard_log.json` (at most 200 entries, data folder) and in the summary (Windows + Telegram). A machine
  whose stop failed is tried again at each following reading while the balance stays below stop (no new grace; quiet until
  it works); `retry` lists them. The pending stop and the button counter are kept in `guard_<id>.json` (data folder): after
  a restart the stop goes on with its deadline (at least a minute away), and buttons of an earlier run never match.
- Only warnings (`stops` false, `no_stop` = why, also the settings note) when the balance isn't money left before a stop:
  Runpod `billingMode` POSTPAID (GraphQL `myself { billingMode }`, read hourly in its own query), Vast.ai without `credit`
  or with `has_billing` (a card on file is charged; instances aren't stopped at $0). A reading without an amount counts as
  unreadable. Before the action probe has answered, a balance below stop is reported once and nothing is stopped yet.
- Balance unreadable (network, 5xx, 401) for 2 readings in a row while the guard is on: one warning, and "readable again".
- Read-only key (probe 403): warnings only. While a stop is pending the balance is read every minute.

## Native CLI `taskhub` (no Python needed; starts the hub if it is not running, disable with `TASKHUB_NO_AUTOSTART=1`)
`taskhub run --name "<what>" --source <agent> [--auto-progress] [--heartbeat N] -- <cmd args>` wraps any command
(echo + log + progress; the command may print `::progress 5/20`, `::percent 40`, `::status s`, `::stage s`, `::warn s`,
`::error s`, `::toast s`, `::metric k=v`, `::widget id=value` lines). Also `notify`, `list`, `show`, `register`, `update`,
`log`, `finish`, `control`, `commands`, `ack`, `wait ID` (blocks until finished), `panel`, `delete`,
`mcp` ([MCP server](#mcp-server)), `hook claude|codex` (agent hooks). `taskhub --help`.

## Claude Code hooks
Easiest: setup dialog checkbox or Settings → *AI agents* (before 2.9: tray → *AI agents*) (or `TaskHubHost.exe --integrations claude=on codex=on`)
writes exactly these entries for you and removes them on uninstall. Manual setup:
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
silently (non-blocking). Toasts follow the usual rules (mute, no pop-ups while the panel is open); clicking one only closes it.

## Codex hooks
Same notifications for [Codex](https://developers.openai.com/codex/hooks): Codex runs command hooks, so use the CLI, which
forwards the event JSON from stdin to `POST /api/hooks/codex` (silent, exit 0, never starts the hub). `~/.codex/hooks.json`:
```json
{
  "hooks": {
    "Stop": [{ "hooks": [{ "type": "command", "command": "taskhub hook codex", "timeout": 10 }] }],
    "PermissionRequest": [{ "hooks": [{ "type": "command", "command": "taskhub hook codex", "timeout": 10 }] }]
  }
}
```
Use the full path (`%LOCALAPPDATA%\Programs\TaskHub\taskhub.exe`) if `taskhub` is not on PATH for Codex. New hooks must be
trusted once in Codex (`/hooks`). Do **not** replace an existing `notify = [...]` in `config.toml` (the Codex app uses it);
`taskhub hook codex` also accepts the legacy `notify` payload as its last argument if you need it.

## MCP server
`taskhub mcp [--source NAME]` is a stdio [MCP](https://modelcontextprotocol.io) server for AI agents with the tools
`notify`, `task_start`, `task_update`, `task_finish`, `task_commands` (button presses / input values, acknowledged on read)
and `task_list`. It starts the hub on the first tool call. Enable it in the setup dialog / Settings → *AI agents* (before 2.9: tray → *AI agents*), or by hand:
- Claude Code (user scope, `~/.claude.json`): `"mcpServers": {"taskhub": {"type": "stdio", "command": "<install dir>\\taskhub.exe", "args": ["mcp", "--source", "claude"]}}`
  (or `claude mcp add --scope user taskhub -- "<install dir>\taskhub.exe" mcp --source claude`)
- Codex (`~/.codex/config.toml`; TaskHub's setup writes the same and keeps the rest of the file):
  ```toml
  [mcp_servers.taskhub]
  args = ["mcp", "--source", "codex"]
  command = "C:\\Users\\<you>\\AppData\\Local\\Programs\\TaskHub\\taskhub.exe"
  ```

## Agent notes (Claude / ChatGPT / Codex / any automation)
Use TaskHub for anything that runs longer than ~1 minute, so the user sees progress and gets notified without watching the chat.
Claude Code / Codex: use the TaskHub MCP tools; if you don't have them, ask the user to enable them and open a new session
(see [AI agents: read this first](#ai-agents-read-this-first)). Everything below is for agents without MCP.
0. Where: CLI `taskhub` (PATH) or `%LOCALAPPDATA%\Programs\TaskHub\taskhub.exe`; HTTP `http://127.0.0.1:47800`; this doc: `taskhub api` or `GET /api/help`.
   If the CLI is not on PATH in your shell, call it by the full path. From WSL/containers use the Windows host address only if the user allows it (the hub listens on loopback).
1. `taskhub health` (or `GET /api/health`) first. If the hub is down and cannot be started, work normally - never fail the user's task because of TaskHub.
2. Long shell command: `taskhub run --name "<what>" --source <claude|chatgpt|codex> [--auto-progress] [--heartbeat N] -- <cmd args>`.
   Inside your own scripts print `::progress i/N`, `::status ...`, `::warn ...` lines - `taskhub run` turns them into panel updates.
   `taskhub wait <id>` blocks until a task finishes (exit 0 = done).
2a. Name things for the user: task `name` = what is being done ("Parameter sweep 1-200"), `source` = who started it.
    Controls with `input` send their value only when the button is pressed, so label them as actions ("Set speed", "Apply").
    Use `"toast": true` only for 1-3 controls the user may need from a notification (Pause, Stop, a key parameter).
2b. Don't add intermediate notifications (`notify.milestones`, `toast` on updates/logs) unless the user asks for them:
    the finish / failure / stall notifications are already on. Never set milestones like [25, 50, 75] by default.
3. For your own Python scripts use `Task(...)` with meaningful widgets; `toast=True` only for events the user must see.
4. Before saying a task is still running, check `GET /api/tasks?active=1&brief=1`.
5. To act on a task: read its `controls`, then `POST /api/tasks/{id}/controls/{cid}` with `{value}` if it has `input`.
