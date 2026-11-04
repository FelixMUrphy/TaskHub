# TaskHub API v1

Local hub for long-running tasks: tray icon + toasts + mini panel with live widgets and controls.
Base URL: `http://127.0.0.1:47800` (env `TASKHUB_URL`). Localhost only. JSON in/out.

## AI agents: read this first
- **Claude Code, Codex and other MCP clients: use the TaskHub MCP tools** (`task_start`, `task_update`, `task_finish`,
  `task_commands`, `task_wait`, `task_list`, `notify`). They need no HTTP, no headers and pass any language correctly.
- **You are Claude Code or Codex but don't see these tools?** Don't fall back to `curl` / HTTP. Tell the user, in one short
  message: *"Enable TaskHub tools (MCP): TaskHub panel → Settings → AI agents → <Claude Code / Codex> — TaskHub tools (MCP), then start a new chat / session -
  MCP tools only appear in sessions started after that."* (Not installed yet: the setup dialog has the same option.)
  Meanwhile continue the user's actual work without TaskHub.
- Only if the user explicitly wants TaskHub in this session anyway, or your client has no MCP (ChatGPT, plain scripts): use
  the **`taskhub` CLI** or the Python client below - both send UTF-8.
- **Never put non-ASCII text (Cyrillic, CJK, emoji, accents) into `curl -d '...'` or a PowerShell 5.1 `-Body '...'` string on
  Windows**: they convert it to the ANSI code page and the user sees `????`. If you must use raw HTTP, send UTF-8 bytes:
  `curl --data-binary @body.json` (file saved as UTF-8) or `-Body ([Text.Encoding]::UTF8.GetBytes($json))`.

**Rules for every client (scripts, Claude, ChatGPT/Codex, PowerShell, curl):**
- Write requests (`POST`/`DELETE`) MUST send header `X-TaskHub: 1` (CSRF guard). Browser requests from other origins are rejected.
- <a id="token"></a>If `config.json` has `"token"` (you set it there yourself, any text, then restart the hub), send `Authorization: Bearer <token>` on every `/api/` request except
  `GET /api/health` and `GET /api/hub-proof` (without it: 401). `config.json` is `%APPDATA%\TaskHub\config.json` (Store edition:
  `%LOCALAPPDATA%\Packages\<package family>\LocalCache\Roaming\TaskHub\config.json`). Env `TASKHUB_TOKEN` for the Python
  client; the `taskhub` CLI and MCP server also read it from the hub's `config.json`. The token keeps out pages in a browser, which a foreign
  `Origin` stops anyway. It is no wall against other programs on this PC: the panel's page `/ui` carries it and is served to anything local.
- `meta.github` and `meta.github_pr` are the connector's: over this API they are dropped from what you send.
- A request the hub can't honour gets a `400` that says why, never a `200` that does nothing ([Errors](#errors)). Check the status of what you send: the `error` text names the field.
- Be fail-soft: if the hub is not reachable (`GET /api/health` fails), keep working without it.
- Throttle updates to ≤ 2–4 per second. Chart points are appended per update, so batch them (see `chart`).
- Always `finish` the task (done/failed/cancelled). If you pass `pid`, the hub marks the task `lost` when the process has been gone for two of its checks in a row (3-6 s) without finishing.

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

**Wrap any command with the Python client (a simpler `taskhub run`, for machines without the CLI):**
```
python taskhub_client.py run --name "Video export" --auto-progress --heartbeat 600 -- ffmpeg -i in.mov -c:v libx265 out.mp4
python taskhub_client.py run --name build --progress-regex "\[(?P<cur>\d+)/(?P<tot>\d+)\]" -- ninja
```
Streams output to the log (and echoes it), parses progress, adds Kill / Ctrl+C / "Send input" (stdin) controls, finishes with the exit code and returns it. Unlike `taskhub run` it has no presets and treats `::progress`-style lines as ordinary output. A regex may name its groups `(?P<cur>...)` or `(?<cur>...)`, here and in `taskhub run`; a wrong one stops with a message before anything runs.

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
| GET | `/api/health` | | `{ok, app:"TaskHub", version, edition: "exe"\|"store", active, notifications, toast_queue}` — `notifications`: Windows' switch for TaskHub's notifications, `enabled` \| `disabled_app` \| `disabled_user` \| `disabled_policy` \| `disabled_manifest` \| `unknown` (when disabled, toasts vanish silently; failure notifications fall back to a tray balloon); `toast_queue`: notifications waiting for the rate limit (`toast_rate` per minute, one per task every `toast_task_gap` s; bursts fold into the latest) |
| GET | `/api/hub-proof?nonce=<16-128 hex>` | | `{proof}` = hex of HMAC-SHA256 with the key in `hub.key` (data folder, 64 hex characters: decode them to the 32 bytes) over `taskhub-hub-proof\|<port>\|<nonce>`; no token needed. Lets a client tell the hub from another program on its port. The `taskhub` CLI and MCP server check it before every request, unless `--url` is given, `TASKHUB_URL` is another address than the hub's own, or it cannot see the key |
| GET | `/api/help` | | this document |
| GET | `/api/tasks` | `?since=V&wait=25` ([long-poll](#long-poll)), `?active=1`, `?brief=1` | all tasks ([fields](#task-fields)) + `version`; `active=1`: only unfinished ones; `brief=1`: only the last 5 log lines of each |
| POST | `/api/tasks` | Registration (below) | create / re-register → `{id}` |
| GET | `/api/tasks/{id}` | | full task ([fields](#task-fields)) |
| POST | `/api/tasks/{id}/update` | Update (below) | progress, status, widgets… |
| POST | `/api/tasks/{id}/log` | `{msg: str or [str], level?: info/warn/error, toast?}` (`message` or `lines` for `msg` work too) | append log, nothing else: other fields are ignored; no text → 400 |
| POST | `/api/tasks/{id}/heartbeat` | | liveness only |
| POST | `/api/tasks/{id}/finish` | `{state (required): done/failed/cancelled, message?, result?, widgets?, source?, agent_cwd?}` | `source` `claude`/`codex` with `agent_cwd` = its folder holds back that agent's next `Stop` toast ([rule](#claude-code-hooks)). finish + toast; the first end stands, a repeated finish changes nothing ([States](#states)); no second toast |
| GET | `/api/tasks/{id}/commands` | `?wait=25` ([long-poll](#long-poll), max 60) | `{commands: [...], state}`: the queued signals for the process. Reading takes them off the queue: each command is returned once, also if you never ack it. Each: `{id, control, value, t, status: "pending", reply: null}`; in the task's `commands` history its status then goes `delivered`, and `ok` / `error` with `reply` after the ack |
| POST | `/api/tasks/{id}/commands/{cmd}/ack` | `{ok: bool, message}`; `{cmd}` = the command's `id` | the process's reply, shown in the panel log (it does not remove or re-queue anything); a later ack replaces it; unknown `{cmd}` → 404 |
| POST | `/api/tasks/{id}/controls/{cid}` | `{value?}` | trigger a control (same as panel button) |
| DELETE | `/api/tasks/{id}` | `?force=1` for active | remove (an active task without `force` → 409) |
| POST | `/api/notify` | `{title, message, level: info/warn/error, task?, source?, agent_cwd?}` | one-off toast; `title` or `message` is required (a missing title shows as TaskHub); `source` and `agent_cwd` as for finish |
| POST | `/api/hooks/claude`, `/api/hooks/codex` | agent hook input (as sent, from `taskhub hook`) | `Stop` / `Notification` / `PermissionRequest` → toast; `{}` for any JSON object (below), 400 only when the body isn't one |
| GET | `/api/telegram` | | Telegram bridge state `{configured, linked, bot, status, away_only, progress, storage, warning}` — `storage`: where the bot token is kept, `vault` (Windows Credential Manager) \| `config` (`config.json`, only when the Credential Manager is unavailable) \| null; `warning`: text for Settings, or null |
| POST | `/api/telegram` | only on a hub run with `TASKHUB_TEST_HOME`, like `/api/discord`: `{token}` · `{test: true}` · `{relink: true}` · `{away_only: bool}` · `{progress: bool}` · `{disconnect: true}` | test hubs; a user sets Telegram up on the settings page: a pairing code gives a chat your notifications and buttons |
| GET | `/api/discord` | | Discord bridge state `{configured, linked, bot, status, phase, away_only, progress, invite, endpoint_set, cannot_write, warning}`; `status`: `off` / `connecting` / `connected` / `offline` / `on hold` / `invalid token` / `closed <code>`; `phase` until linked: `server` (no shared server, or its direct messages are off) / `confirm` (the button was sent) / `retry` (Discord failed or is out of reach) / `again` (restarted before the button was pressed). `invite` has the application's id, as any invite link does; never the token or other ids. Setting it up is only on the settings page, not over HTTP |
| GET | `/api/connectors` | | every connector's state: `{<connector id>: {...}}`, each as below |
| GET | `/api/connectors/github` | | connector state: `{enabled, repos, interval, notify_success, logs, auth, token_set, token_check, token_check_text, login, token_expires, status, repo_status, rate_remaining, active_runs, pr_reviews, pr_dependabot, prs}` (the token itself is never returned); `repo_status` = `{repo: "ok" or a line in the UI language or "HTTP n"}`, for showing: don't compare the text |
| GET | `/api/connectors?view=settings` | | the rows of the settings page's Connectors section: `{connectors: [{id, name, kind, enabled, uses_key, key_set, key_check, summary, help_url, thresholds?, fields?, guard?, setup?, key_parts?, list?, switches?, notes?, help_needed?}], lang}` - `key_parts` = a key entered in several fields, sent joined with `:`; `key_check` = the last verdict on the saved key (`ok` / `revoked` / `denied` / `clock` / `network` / `unavailable` / null), `summary` = the state line in the UI language ("Connected, balance $X", "Connected as @login · 3 repositories"...), `help_url` = the "how to get a key" page (`docs/keys/<id>.md`, or `<id>.<lang>.md` when that language exists); `fields` = the connector's number inputs as the page draws them: `[{key, label, min, max, step, value, empty_ok?}]` (`label` = a key of the panel's translations; `thresholds` is there as well); never a key |
| POST | `/api/connectors/github` | `{enabled?, repos?: ["owner/repo"], interval?: 15-3600, notify_success?, logs?, auth?: "token"\|"gcm", token?, forget_token?, pr_reviews?, pr_dependabot?}` | GitHub Actions connector (below) |
| GET | `/api/connectors/runpod` | | `{enabled, key_set, key_check, key_check_text, status, can_act, probed_at, machines: [{id, name, state, task}], running, balance: {amount, spend_per_hr, spend_limit, runway_hours, raw, at}, low, idle_minutes, idle_util, low_runway_hours, low_balance, graphql_balance, summary}` (the key is never returned) |
| POST | `/api/connectors/runpod` | `{enabled?, key?, forget_key?, idle_minutes?: 1-1440, idle_util?: 1-50, low_runway_hours?: 0-720, low_balance?: $\|null, list_interval?: 30-3600, detail_interval?: 15-3600, billing_interval?: 300-86400, graphql_balance?}` | Runpod connector (below). `idle_minutes`, `idle_util`, `billing_interval`, `graphql_balance` and the guard's keys answer 403 here: Settings only |
| GET | `/api/connectors/vastai` | | the same as Runpod without `graphql_balance`; `balance.label` says what `amount` is ("Balance $x · Credit"), `balance.raw = {balance, credit, ...}` (the key is never returned) |
| POST | `/api/connectors/vastai` | `{enabled?, key?, forget_key?, idle_minutes?, idle_util?, low_runway_hours?, low_balance?, list_interval?, detail_interval?, billing_interval?}` (ranges as Runpod) | Vast.ai connector (below). `idle_minutes`, `idle_util`, `billing_interval`, `graphql_balance` and the guard's keys answer 403 here: Settings only |
| GET | `/api/connectors/aws` | | `{enabled, key_set, key_check, key_check_text, status, regions: {<region>: {state, code, count, read_at}}, regions_chosen, machines: [{id, name, region, state, task}], running, forgotten_hours, forgotten_exempt, list_interval, sagemaker, batch, job_interval, jobs: [{kind, id, name, region, state, task}], job_polls, cost, budgets, cost_read: {state, code, period, month, forecast, unit, read_at, next_at}, budgets_read: {state, code, read_at, items: [{name, actual, limit, forecast, unit}]}, summary}`; never a key |
| GET | `/api/connectors/ssh` | | SSH connector (below): `{enabled, client, interval, disk_warn, tmux_notify, hosts: [{host, state, text}]}` |
| POST | `/api/connectors/ssh` | | 403: Settings only; an empty body changes nothing and gives the state |
| POST | `/api/connectors/aws` | `{enabled?, key?: "id:secret", forget_key?, regions?: [..], forgotten_hours?: 0-8760, list_interval?: 300-3600, sagemaker?: bool, batch?: bool, job_interval?: 120-3600, cost?: bool, budgets?: bool}` | changes the settings; a key AWS rejects -> 400, nothing saved; `cost: true` -> 403, it goes on only in Settings because it costs money (`cost: false` works) |
| POST | `/api/connectors/runpod`, `/api/connectors/vastai` | the connector's own fields ([Runpod](#connectors-runpod), [Vast.ai](#connectors-vastai)) | changes its settings; any invalid value → 400 and nothing is saved. The [balance guard](#balance-guard)'s fields and `idle_stop_minutes` are refused here (403): they change only in Settings |
| GET | `/api/guard/log` | `?provider=runpod\|vastai&limit=1-200` (50) | what the balance guard stopped, newest first: `{entries: [{t, provider, machine_id, name, trigger: "auto"\|"stop_now"\|"idle", balance, threshold, idle_minutes, burn_per_hr, spent, spent_estimated, result: "stopped"\|"failed", error, storage_per_day, storage_note, storage_approx}]}` |
| GET | `/api/limits` | | plan limits as last reported: `{claude: {five_hour: {used_percentage, resets_at, at}, seven_day: {...}}, codex: {...}}`, only agents with their switch on; also `limits` and `limits_warn` in `GET /api/tasks`; empty while the switch is off |
| POST | `/api/limits/claude` | `{five_hour?: {used_percentage, resets_at?}, seven_day?: {...}}` | a reading, from `taskhub statusline`; only these numbers are kept; ignored while the switch is off (`{ok: true, ignored: true}`); a `resets_at` further off than its window's length counts as none; a body with no limit window → 400 |
| POST | `/api/clear` | | remove finished tasks |
| POST | `/api/settings` | `{muted?: bool, seen?: true \| [task ids], language?, theme?: "system"\|"light"\|"dark", accent?: "system"\|"taskhub", panel_transparency?: 0-100, card_transparency?: 0-100, panel_group?: "project"\|"source"\|"none", panel_pinned?: bool}` | mute toasts; panel look (also in `GET /api/tasks` → `panel`, which also shows `panel_push_windows`: read-only, it changes only on the panel's settings page) |
| POST | `/api/ui/open` | `{task?}` | open panel (focus a task) |

### Long-poll
- `GET /api/tasks?since=V&wait=S`: `V` is the `version` from your previous reply. The hub answers as soon as its version is
  above `V`, or after `S` seconds (at most 55) with the same full reply. Without `since` (or `since=0`) `wait` is ignored and
  the reply comes at once.
- `GET /api/tasks/{id}/commands?wait=S`: answers as soon as a command is queued, or after `S` seconds (at most 60) with
  `commands: []`.

## Errors
Every error is JSON `{"error": "<text>"}` with one of these statuses:

| Status | When |
|---|---|
| 400 | the body is not a JSON object (a raw `NaN` isn't JSON), or a field is invalid: a required one missing, a wrong type, a `level` or `state` that doesn't exist, a value for a widget the task doesn't have, an `update` with no known field in it. The `error` text says which; nothing has changed. A field not known yet beside known ones is no error |
| 401 | a token is set and the request has none or a wrong one ([token](#token)) |
| 403 | a write request without `X-TaskHub: 1`, a foreign `Host` / `Origin`, a control that works only in the panel, a connector's setting that changes only in Settings |
| 404 | unknown task, control, command, connector or route |
| 409 | the task's state doesn't allow it: deleting an active task without `force`, a control not available in this state, a refused kill (also on a task with no `pid`) |
| 413 | the body is larger than 2 MB |
| 503 | a feature that is not available right now (the Telegram bridge, connectors in a hub started without them) |
| 500 | anything else (also a kill that didn't stop the process) |

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
                                          // no Kill and no lost-detection when the hub can't read the process's start time (not a Windows pid):
                                          // use heartbeat_timeout there
  "heartbeat_timeout": 600,               // s without update -> "stale" + toast (0 = off)
  "expected_duration": 120,               // s it usually takes: the ETA comes from it (not from the pace) until overdue
  "notify": {"start": false, "finish": true, "fail": true, "stale": true, "milestones": []},
                                          // milestones: e.g. [50] -> one toast at 50 % - only if the user asked for it;
                                          // milestone toasts are plain, "milestone_controls": true adds the task's toast controls
  "cwd": "E:/proj", "links": [{"label": "results", "url": "file:///E:/proj/out"}], "meta": {},
  "controls": [],                         // see Controls
  "widgets":  [],                         // see Widgets
  "progress_toast": false                 // one live Windows notification with a progress bar, updated in place
}
```
**Registering an `id` that exists** replaces the task: it is `running` again, and `name`, `total`, `controls`, `widgets` and the
rest come from the new request only (what you leave out is gone; progress and `status` start empty). Kept: `log`, `metrics`,
`created`, the command history and the points of a `chart` declared again with the same `series`. `started` is kept while the old
task is active and starts anew when it had finished. Signals the process hasn't read yet are dropped.

## Update `POST /api/tasks/{id}/update` (all fields optional)
`progress` 0..1 · `percent` 0..100 · `current` (+`total`) - when several are sent they apply in the order `current`/`total` →
`percent` → `progress`, so the last one wins · `status` · `stage` · `log` (str or [str]) + `level` (info/warn/error) ·
`toast`: true or string → Windows notification now · `metrics`: an object `{k: v}` (a null value deletes the key; the panel shows it as a key-value list) ·
`widgets`: `{widget_id: value}` · `state`: running/paused · `project` · `attention` (string; `""` clears, also cleared on finish) · `add_widgets` / `remove_widgets` · `add_controls` / `remove_controls` / `controls` (replace) · `links` · `meta` · `expected_duration`.
`links` (register and update): a list of URLs or `{"label", "url"}`. Only `http`, `https`, `mailto` and `file` URLs or an absolute
path (`E:/proj/out`, `\\server\share\x`) - anything else is rejected with 400. The panel opens web links in the browser and shows
a file or folder link in Explorer (a folder opens, a file is selected in its folder) - it never runs a linked program.
An update with an invalid field is rejected as a whole (400, nothing changed): an invalid entry in `controls` / `add_controls` / `add_widgets` / `links`, a text in
`progress` / `percent` / `current` / `total`, a `state` other than running/paused (`finish` ends a task), a value for a widget not declared. A number sent as text
(`"40"`) is taken as the number. `NaN` / `Infinity` (also as text, `"inf"`) in those fields and in numeric widgets mean "no value": they are ignored, the rest of the
update applies and what was there stays. The Python client leaves them out before sending, because a raw `NaN` isn't JSON. A finished task keeps its state:
an update can still change its other fields.

## Task fields
What `GET /api/tasks/{id}` returns. Fields not listed here may change.

| Field | Meaning |
|---|---|
| `id`, `name`, `source`, `description`, `project`, `unit`, `pid`, `cwd`, `host`, `meta`, `links`, `notify`, `heartbeat_timeout`, `expected_duration`, `progress_toast` | as registered |
| `state` | see [States](#states) |
| `created`, `started`, `updated`, `last_seen`, `finished` | Unix time in seconds; `finished` is null until the end |
| `progress` | 0..1 or null; `current`, `total`: numbers or null |
| `status`, `stage`, `attention`, `message`, `result` | as last sent; `message`, `result`: from `finish` |
| `rate`, `eta_at` | pace and expected end, once the hub can tell; gone at the end |
| `metrics` | `{key: value}` |
| `log` | `[{t, level, msg}]`, newest last; the hub's own lines have level `sys`, button presses `cmd` |
| `controls`, `widgets` | as declared, with `last_value` / `value` and for charts `points` |
| `commands` | the history of button presses, see `GET /commands` |
| `milestones_hit`, `seen` | for the panel |

Limits (longer text is cut, older lines dropped): `id` 64 characters of `[A-Za-z0-9_.-]`, `name` 200, `description`, `status` and `stage` 1000, `message` 2000,
`project` 80, `attention` 300, `unit` 20, a metric's key 60 and text value 200, `max_points` of a chart 2000, the log 400 lines
(`log_lines` in `config.json`); a batch of log lines or chart points keeps only its newest ones up to those limits.
A task of a built-in connector (`source` github, runpod, vastai, aws, ssh) keeps its buttons: `controls`, `add_controls`, `remove_controls` on it, or registering over its id, → 403. `meta.<connector id>` and `meta.github_pr` in a request are dropped on every task: they say what a connector's buttons act on, and only the connector sets them.
More than these → 400: 40 widgets, 64 controls, 20 links, 20 `notify.milestones`, 100 metrics a task, 64,000 characters of JSON in `meta` and in a widget's value (a chart's points don't count),
2000 active tasks on the hub (an id that exists can always start again). A request's body is limited to 2 MB (413).

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
| `progress` | `total` | without `total`: up to 1 is a fraction (1 = 100 %), above 1 is a percentage; with `total`: a count; also `{value, total, label}` |
| `gauge` | `min`, `max`, `warn`, `crit`, `invert` | number (half-circle) |
| `stat` | `warn`, `crit`, `invert` | number; shows ▲▼ delta vs previous |
| `led` | `text` | `"ok"`/`"warn"`/`"error"`/`"busy"`/`"off"`, bool, or `{state, text}` (`{value, text}` too) |
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
Declared by the task, rendered as buttons. Fields: `id` (required, `[A-Za-z0-9_.:-]`, up to 48 characters, else 400), `label`, `kind` (default `signal`), `style` (default/primary/danger),
`confirm` (question asked before the control runs - a dialog in the panel, a Yes / No notification for toast buttons, a Yes button
in Telegram), `hint`, `states` (when shown; default running/paused/stale), `input` (value prompt),
`toast: true` (also show this control as a button in the task's Windows notifications; `input` becomes a text/combo box there).

| kind | on click |
|---|---|
| `signal` | queued for the process → it receives via `GET /commands?wait=25` → replies with `ack` |
| `shell` | hub runs `command` (list or string; placeholders `{value}` `{pid}` `{task_id}`; see *Shell values* below), the last 25 output lines and the exit code → task log; `cwd`, `timeout` (s, default 300, 1–86400) |
| `open` | hub opens `target` (file, folder, URL) |
| `connector` | only on tasks of a built-in connector (not accepted from API clients): the connector does it (e.g. cancel / re-run on GitHub); `panel_only` ones (delete a Runpod pod / Vast.ai instance) answer 403 over HTTP - only the panel's own page can trigger them |
| `kill` | hub kills `pid` with its whole process tree (only if it is still the process the task registered, never a reused pid); task → cancelled. Refused (409) when the process's start time couldn't be read at registration; if the process is still running after the kill (e.g. it runs elevated: access denied) → 500 with the reason and the task stays as it was |

`input`: `{"type": "text"|"number"|"select"|"bool", "label", "default", "options": [...], "min", "max", "step"}`.
Values are checked by the hub for every surface (400 otherwise): a number within `min`/`max`, `true`/`false` for bool, one of
`options` for select, text as a string (cut to 2000 characters).
The value is sent only when the control's button is pressed, so give such controls an action label ("Set speed", "Apply", "Send"),
and `input.label` for the field itself. The hub remembers the last sent value (`last_value` in the control) and pre-fills the panel form
and the toast input with it.
**Shell values**. The value (from the panel, a notification, Telegram or Discord) is never pasted into a command line as text:
- every form: the hub also passes it in the environment variable `TASKHUB_VALUE` (absent when the value is empty);
- string `command` (run by `cmd.exe /d /s /c`): `{value}` becomes `!TASKHUB_VALUE!` and cmd runs with delayed expansion (`/v:on`),
  which expands it only after the line is split into commands, pipes and redirections - `& | < > ^ % " !` in the value stay plain
  text (an empty value becomes nothing). Because of `/v:on`, `!` and `^` written in such a template itself are special to cmd (write `^!` for a literal `!`). The value still
  reaches the program as words: a value with spaces is several arguments unless the template quotes it (`"{value}"`) - for exact
  arguments use the list form;
- list `command`: each item is one argument (quoted for the program). `{value}` can't be in the first item (the program), and a
  `.bat` / `.cmd` program can't take `{value}` in a list (cmd.exe would parse it) - both rejected with 400.

Command delivered to the process: `{"id": "a1b2c3", "control": "lr", "value": 0.001, "t": 1793240199.6, "status": "pending", "reply": null}`. A control with `input` pressed without a value sends `null`.
Python client built-ins: `stop` → `t.stop_requested`; `pause`/`resume` (`pausable=True`) → `t.paused`, `t.wait_if_paused()`.

### Ask the user and read the answer
Over HTTP, without MCP: (1) `POST /update` with `attention` (the question, puts the task on top of the panel) and `add_controls` with a `signal` control with `input`, `"toast": true` if a
notification should say so; (2) read `GET /commands?wait=25` in a loop until the command comes; its `value` is the answer; (3) `POST /commands/{id}/ack`, then `POST /update` with
`"attention": ""` and `remove_controls`. The user may answer from the panel, Telegram or Discord: it arrives the same way.

## States
`running` · `paused` · `stale` (no `update`, `log` or `heartbeat` for `heartbeat_timeout`; any of them brings the task back to `running`) · `done` · `failed` · `cancelled` · `lost` (pid gone without finish). The first real end stands: a second `finish` over this API changes nothing and answers `{ok: true, state: <what stands>, already_finished: true}`, so a process's "exit code -1" after a Kill leaves the task `cancelled`. Only `lost` gives way, when the process reports after all; `lost` itself is the hub's and can't be sent. Registering the same `id` again starts anew.
Toasts: finish / fail / stale / milestones (per-task `notify`), `toast` in update/log, `/api/notify`. Global mute in tray or panel.

## Local log
`taskhub.log` in the data folder, for diagnosis; there is no API for it. A line is `yyyy-MM-dd HH:mm:ss.fff LVL area text`, with `LVL` one of `DBG`, `INF`, `WRN`, `ERR`.
`"log_level"` in `config.json`, read at start: `"info"` (default) or `"debug"`, which adds notification text, task names and panel show / hide; anything else keeps the default.
One rotated copy, `taskhub.log.1`, past 4 MB. At `debug` the CLI also writes its own failures to `cli.log` (hooks and MCP are silent otherwise).
A beta build (`build.cmd beta`) defaults to `debug` and keeps three copies of 20 MB. Text that looks like a token or key is masked at any level.

## Discord

A mirror of notifications, like Telegram. Settings page only (token, *Check again*, *Send test*, *Disconnect*, switches). Controls pressed there arrive with `via` `discord` and follow the same rules as Telegram's (panel-only controls are refused).

Config keys: `discord_away_only` (false), `discord_away_minutes` (5), `discord_progress` (true); set by linking: `discord_user`, `discord_channel`, `discord_app`, `discord_cmds` (ids as strings). `discord_api`, `discord_identify_gap`: tests only, honoured only for a loopback address.

Outbound: `https://discord.com/api/v10` and `wss` to `gateway.discord.gg` / `*.discord.gg`, checked before each connection, the address Discord names for resuming too. Nothing is polled; presses arrive over the gateway, with intent `GUILDS` only. A new session waits at least 5 minutes after the last, and holds below 100 left in Discord's daily count. After a 401 nothing is sent until the token changes; after a 403 on the chat nothing until *Send test* or *Check again*. *Send test* sends at once and reports Discord's answer.

## Connectors: GitHub Actions
Panel → ⚙ Settings → Connectors → GitHub Actions (or `POST /api/connectors/github`). Each workflow run of the chosen repositories is a task:
id `gh-<run id>`, source `github`, project = repository name, description `owner/repo · branch · sha · @actor · event`;
`stage` = job › step; ETA and progress both from the median duration of the workflow's last successful runs (past it: no `progress`, the status says "longer than usual"); without such runs, or waiting for an approval, progress by the steps of all jobs;
on failure the message names the job › step, and (option `logs`) the last 30 lines of its log go to the task log.
Buttons: open on GitHub, cancel (running), re-run failed jobs (failed), re-run all (finished), each with a confirmation; links to
the artifacts and to the release of a tag after a successful run. Failures always notify (Windows + Telegram), successes if
`notify_success`. Runs that finished before TaskHub started are not shown; a re-run (also one started on github.com) shows the task again.
Access: your own fine-grained token (Actions: Read and write, Contents: Read-only, Metadata: Read-only; only these repositories),
kept in the Windows Credential Manager - or `auth: "gcm"`, the Git Credential Manager's github.com token (broad rights).
Requests go only to api.github.com (with the token) and, for logs, to GitHub's log storage through the one-time link GitHub gives
(without the token). Polling uses ETags (unchanged = free): every `interval` s (default 60) when idle, every 10 s while a run is
going, slower near the rate limit. A notification warns 7 days before the token expires.

Pull requests, read-only (`pr_reviews`, `pr_dependabot`, both off by default): PRs waiting for your review (search
`is:pr is:open archived:false review-requested:<login>`, team requests included, at most once a minute - the search API's own
limit of 30 / min; a search rate limit pauses only these searches) and open Dependabot PRs (`dependabot[bot]`) in the
listed repositories (`GET /repos/{repo}/pulls?state=open`, ETag, every 5 min). Each is a task `ghpr-<12 hex>` (hash of
`owner/repo#n`), source `github`, name `Review · title` / `Dependabot · title`, description `owner/repo #n · @author`,
`attention` for a review request, link + Open button only, `meta.github_pr {kind, repo, number, title}`. A new one logs and
notifies; when it leaves the list the task ends `done` (no notification) with `Merged` / `Closed without merging` /
`No longer waiting for your review` / `No longer open` (read from `GET /repos/{repo}/pulls/{n}`). Turning a switch or the
connector off ends its tasks. Nothing is ever written to GitHub. Permissions: the search needs none (a fine-grained
token sees review requests only in its own repositories and public ones); the PR list needs Pull requests: Read-only (no access: the task ends `No access to the repository's pull requests`,
`pr_status {repo: text}` says so, unless `repo_status` already says that the token can't reach the repository at all); a repository taken off the list ends its tasks (`The repository is no longer watched`).
More than 100 open PRs / review requests are paged; a list too long to read ends nothing. State also has `pr_status`.

## Connectors: Runpod
Panel → ⚙ Settings → Connectors → Runpod (or `POST /api/connectors/runpod {key, enabled: true}`). The key is checked with
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
Panel → ⚙ Settings → Connectors → Vast.ai (or `POST /api/connectors/vastai {key, enabled: true}`); the key is kept like
Runpod's. Each instance is a task: id `va-<instance id>`, source `vastai`, project `Vast.ai`, name = its label (else
"instance <id>"), `meta.vastai = {id, name, state, status}`. `actual_status` running → active; `intended_status` running while
not yet running (loading, scheduling...) → active, *Starting…* (after a minute in *Scheduling*: "waiting for its GPU"); asked to
stop / exited → `done` "stopped"; offline → `failed`; destroyed / gone from the list → `done` "terminated". Widgets as Runpod
plus a disk series in `load` (CPU, RAM, Disk); load values Vast.ai leaves null are null points. Uptime from `uptime_mins`, else
`start_date`. Link: the Vast.ai console. Buttons (probe: `PUT /api/v0/instances/0/ {label}` - 403 = read-only): stop / reboot
(running), start (stopped), delete = destroy (panel only, typed name) - as Runpod. Vast.ai's `200 {"success": false, msg}`
answers count as errors (its `msg` is shown). Spent = `/api/v0/charges` of the last 30 days (≈); balance and credit from
`/api/v0/users/current/` (credit, when present, is `amount`). Polling as Runpod (list paged 25 at a time); a 429 (Vast.ai sends
no Retry-After) pauses everything 60 s, doubling while 429s follow each other (up to 30 min).

## Connectors: SSH
*Off by default.* Your own Linux hosts, read-only, through Windows' `ssh.exe` with your ssh config, agent and `known_hosts`; TaskHub keeps no key. Hosts, interval and the switch change only in Settings: `POST /api/connectors/ssh` answers 403.
`GET /api/connectors/ssh` gives `{enabled, client, interval, disk_warn, tmux_notify, hosts: [{host, state, text}]}`. `state`: `connecting`, `ok`, `unreachable` (retried after 1, 2, 5, then every 15 min), or, tried again only after the *Check again* control: `hostkey`, `hostkey_changed`, `auth`, `restricted`, `not_linux`, `no_client`.
Each host is a task `ssh-<host>`, source `ssh`. Controls: `watch` and `unwatch` (`{value: PID}`), `recheck`. Config (`connectors.ssh`): `enabled`, `hosts` (20 at most; up to 60 letters, digits, `.`, `-`, `_`), `interval` (30-3600 s, 60), `disk_warn` (0-100 %, 90, 0 = off), `tmux_notify` (true), `watched`.

## Connectors: AWS
*Built from AWS's documentation and tested against a simulator, not yet on a real account.* Panel → ⚙ Settings → Connectors → AWS. The key is two parts (`id:secret`), checked with
AWS, kept in the Windows Credential Manager and never returned; temporary `ASIA` keys are refused. Read-only: no controls change anything in AWS.

`key_check`: `ok` | `revoked` | `denied` (a real key without `ec2:DescribeRegions` and no Regions listed) | `clock` | `network` | `unavailable`; only a key AWS rejects isn't saved. `regions` empty = every Region enabled in the account (DescribeRegions at `us-east-1`, daily, at most 40). `regions.<r>.state`: `ok` | `no_access` | `throttled` | `clock` | `error`; a Region that fails declares nothing gone. Defaults: `list_interval` 600 s, `job_interval` 300 s, `forgotten_hours` 72 (0 = off). `job_polls`: `{"<region>/<service>": {state, code, active}}`, `state` also `not_offered`.

An instance is active while `pending`, `running`, `stopping` or `shutting-down`; `stopped` → `done`, "stopped"; missing from two reads in a row or `terminated` → `done`, "terminated". An instance first seen stopped shows without a notification. Past `forgotten_hours`: `attention`, a log line and a notification (once per run, at most one per 10 minutes), and the button `keep` (adds it to `forgotten_exempt`); `remind` undoes it. Both only change TaskHub's settings. Cards link to the Region's console page, for instances and jobs alike.

Tasks: `aws-<instance id>`, source `aws`, project `AWS`, `meta.aws = {id, region, state, ...}`. Long jobs behind switches: `aws-sm-<name>-<hash>` (SageMaker training job; no
percent, `status` is the phase) and `aws-b-<job id>` (Batch; `progress` only for array jobs, from the children's counts). Completed / SUCCEEDED → `done`; Failed / FAILED,
or stopped by a time limit → `failed`; Stopped, or a cancelled Batch job → `cancelled`. `meta.aws.kind` = `sm` / `batch`. Jobs finished before the switch (or the connector) was
turned on aren't shown; a job finished while TaskHub was closed shows without a notification. A read that fails or is refused declares nothing finished; a job AWS says no longer
exists is let go (`done`, "No longer watched"). A mass let-go (another key, connector off, a switch off) sends one summary.

`cost_read.state`: `ok` | `not_ready` (no data yet) | `no_access` | `error`; `month` / `forecast` are `null` when not known, never 0 for an error. `forecast` includes what is
already spent. Cost Explorer is billed by AWS ($0.01 per request), so its switch goes on only on the settings page, not over this API: at most 2 requests every 8 hours, remembered across restarts and key changes; if that can't be saved, it isn't read. Sooner only if the network broke and nothing reached AWS, or after the switch is turned on again or the key is changed, following a refusal. Only `COST` and `USAGE` budgets are read. `budgets_read` is read every 6 hours; a budget over its limit
sends one notification a month. Fields not listed here may change.

## Balance guard
*Off by default. `guard*`, `idle_stop_minutes`, `idle_minutes`, `idle_util`, `billing_interval` and `graphql_balance` are refused over this API (403): they decide when machines stop, so they change only in Settings. Reading them stays.* Per machine connector (`connectors.runpod`, `connectors.vastai` in `config.json`): `guard` (false),
`guard_warn` ($, default 10), `guard_stop` ($, default 2; `guard_warn` > `guard_stop` >= 0), `guard_grace_min` (10, 1-120),
`guard_snooze_min` (15, 5-240), `guard_exempt` (machine ids that are never stopped by the guard). `low_runway_hours` /
`low_balance` keep their meaning and stay in the panel; while the guard is on, their warning is skipped only for a reading
the guard's own warning covers (balance ≤ `guard_warn`), so the two never fire for the same crossing.
Burn = Runpod's `currentSpendPerHr`, else the sum of the running machines' $/h (Vast.ai: `dph_total`); hours to stop =
(balance − stop) / burn. The balance is read every `billing_interval`, sooner as the stop threshold nears (a third of the time left, at least 60 s apart), every 60 s while a stop is pending or failed; not sooner when the guard can't stop anything (read-only key, an account the provider doesn't stop at $0, nothing stoppable running). While the service's figure stands still it is carried down at the burn for the stop decision, for at most 900 s; `balance` and `hours_to_stop` stay the service's figure. A pending stop is cancelled when the figure rose above the threshold, or nothing is being spent. A 200 without the machine list, Vast's 200 `success: false`, or a next page without a cursor counts as a failed list: no machine is declared gone. Runpod's billing mode is asked at each reading until known, then hourly; the last known mode stays while the question fails; unknown at a stop: treated as prepaid, the announcement says so. Idle stop on Vast.ai waits until the machine's `gpu_util` was above 1 (`idle_stop_minutes` otherwise only logs that it waits). `can_act` false comes from the service's own 403 (a JSON answer) to an action or the probe; the probe is then repeated after 2 minutes, doubling up to an hour, and 400 / 404 / 405 / 409 / 422 bring it back. A 403 without JSON (a challenge page) leaves `can_act` as it was. `GET /api/connectors/<id>` → `guard: {enabled, warn, stop, grace_min, snooze_min, exempt, can_act,
balance_source, burn_per_hr, hours_to_stop, stop_at, zero_at, unreadable, warned, stops, no_stop, retry: [machine id],
pending: {seq, deadline, held, capped, machines, task, kind: "balance"|"idle"}|null}`; the settings rows (`?view=settings`) carry `guard` with the texts in the UI language (forecast,
`hint` under the stop threshold (Runpod: the balance moves in steps), pending line, machines with `exempt`, the last log lines, a note when it can only warn).
- Auto-stop on GPU idle (`idle_stop_minutes`, 0 = off, else 5-1440; works with `guard` off): a running machine
  whose GPUs all stay under `idle_util` that long gets a pending stop of kind `idle` (same task, Stop now / Snooze,
  `guard_grace_min`, `guard_exempt`); the GPU busy again or the machine no longer running cancels it; at the deadline it
  is stopped only if still idle - a load reading taken after the announcement and recent (unknown load: cancelled, not
  stopped) and idle for at least the current `idle_stop_minutes` (raised meanwhile: cancelled); log `trigger: "idle"`,
  `idle_minutes`, `threshold: null`. One pending stop at a time; a balance stop replaces a pending idle one; a cancelled
  one comes back while the machine stays idle. Not kept over a restart. Needs a key that may run actions.
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
`taskhub run --name "<what>" --source <agent> [--auto-progress] [--heartbeat N] [--preset blender|none] -- <cmd args>`
wraps any command (echo + log + progress; presets read a known tool's output - `blender`: frame / samples status,
overall progress when the frame range is known, widgets `blender_spf` (chart, s per frame), `blender_mem` (chart, MB) and
`blender_saved`, control `blender_stop` ("Stop after this frame": ends Blender when the next frame begins after a saved one,
task `cancelled`; pressed during the last frame: Blender ends by itself, `done`); after a stop / crash / error in an animation the message ends with `resume: -s N` and `run` prints the
command to resume on stderr; Blender's `Error:` lines (except the leak detector's "Not freed memory blocks", logged as a warning) fail the task and `run` exits with 1 although Blender exits with 0; video frames ("Append frame N") and stereo view files count by frame; chosen by the command's file name
unless `--progress-regex` / `--status-regex` / `--auto-progress` is given; the command may print `::progress 5/20`, `::percent 40`, `::status s`, `::stage s`, `::warn s`,
`::error s`, `::toast s`, `::total n`, `::log s`, `::metric k=v`, `::widget id=value` lines; a `::widget` id not seen yet becomes a text widget, up to 40; an id the hub wouldn't take makes it an ordinary line). The command gets `TASKHUB_URL` and `TASKHUB_TASK_ID` in its environment. A wrong `--progress-regex` / `--status-regex` stops with exit code 2 before anything runs. `taskhub --help` lists all flags. Also `notify`, `list`, `show`, `register`, `update`,
`log`, `finish`, `control`, `commands`, `ack`, `wait ID` (blocks until finished; exit code 0 done, 1 failed or lost, 2 cancelled or a task it can't find, 124 its timeout), `panel`, `delete`,
`mcp` ([MCP server](#mcp-server)), `hook claude|codex` (agent hooks), `statusline` ([plan limits](#plan-limits-claude-code)). `taskhub --help`.

## Claude Code hooks
Easiest: setup dialog checkbox or Settings → *AI agents* (or `TaskHubHost.exe --integrations claude=on codex=on`)
writes exactly these entries for you and removes them on uninstall. When an agent is used on the PC, its hooks are off and no choice was made,
the panel shows a card with *Connect* / *Not now* and one notification says so; nothing is written before *Connect* (`hooks_offer` = `[{agent, name}]`, `hooks_offer_tg` = whether these notifications also go to Telegram, in
`GET /api/tasks`; the choice can't be made over HTTP; `integration_claude` / `integration_codex` in `config.json` hold it, `hooks_offer_told` the agents already told about).
`Stop` shows no toast if the same agent has just (30 s, once) sent a notification or finished a task through TaskHub's MCP server (one whose `notify` allows that finish notification) from the same project folder; other events are never held back. Manual setup:
Notifications when Claude Code finishes a response (`Stop`, toast shows the first line of the answer and the project folder)
or waits for a permission / your answer (`Notification`). Add to `~/.claude/settings.json` (or install the
[taskhub-claude-code](https://github.com/FelixMUrphy/taskhub-claude-code) plugin, which sends the same events over HTTP):
```json
{
  "hooks": {
    "Stop": [{ "hooks": [{ "type": "command", "command": "C:\\path\\to\\taskhub.exe", "args": ["hook", "claude"], "timeout": 10 }] }],
    "Notification": [{ "matcher": "permission_prompt|elicitation_dialog",
      "hooks": [{ "type": "command", "command": "C:\\path\\to\\taskhub.exe", "args": ["hook", "claude"], "timeout": 10 }] }]
  }
}
```
`taskhub hook claude` forwards the event JSON from stdin to the hub: silent, exit 0, nothing back to Claude Code, whatever answers. TaskHub writes this form with no shell (`args`; a recent Claude Code) and replaces its older HTTP entries, because an HTTP hook acts on whatever answers its URL. If TaskHub is not running nothing happens. Toasts follow the usual rules (mute, no pop-ups while the panel is open); clicking one only closes it.

## Plan limits (Claude Code)

Off by default: Settings → AI agents → Claude Code → *plan limits in the panel*, or `TaskHubHost.exe --integrations claude-limits=on`. It sets Claude Code's
`statusLine` in `~/.claude/settings.json` to `taskhub statusline`, keeping the entry's other fields. Your own entry is kept in `~/.claude/statusline.taskhub.json` and put back as it
was when you turn this off; if the command there is no longer TaskHub's, it stays. The switch is what the file says at that moment, also when it changes while the hub runs.

`taskhub statusline` runs your command in the same shell with the same input and returns its output and exit code unchanged (a command still running after 15 s is ended). It
sends the hub only `rate_limits.five_hour` and `seven_day`: `used_percentage` and `resets_at`. It never starts the hub. Warnings: `limits_warn` in `config.json`, default `[75, 90]` (also the panel's yellow and red; the highest is an error-level warning),
once per limit window. Several sessions may report different numbers: `resets_at` moving on by more than 5 minutes is a new window, an earlier or past `resets_at` is ignored,
and within a window the highest percentage stands. Only readings without `resets_at` take a fall of more than 1 point as a reset.

Claude Code gives these numbers only on Pro, Max and Ultra plans and after the first API response, so nothing is known while no session runs, and any limit a model has of its own isn't included.
A project's own `statusLine`, `disableAllHooks` or `allowManagedHooksOnly` can keep this command from running.

## Plan limits (Codex)

Off by default: Settings → AI agents → Codex → *plan limits in the panel*, or with the hub closed `TaskHubHost.exe --integrations codex-limits=on` (`limits_codex` in `config.json`; with the hub running this exits 1 and changes nothing). Nothing is written to Codex's settings.

Codex has no status line, so TaskHub asks Codex itself: once when you turn this on, then every 10 minutes while a `codex` process is running (checked once a minute). It starts `codex app-server` with no window, sends `initialize`, `initialized` and `account/rateLimits/read`, and closes it (about a second; a Codex that doesn't answer in 20 s is ended). No thread is started, so there are no model requests and your MCP servers aren't started. Codex uses its own login and asks OpenAI for the numbers; a read also adds to Codex's own logs and caches in `CODEX_HOME`.

Windows are told apart by length (300 and 10080 minutes), so a plan with no 5-hour window shows only the week. Only `used_percentage` and `resets_at` are kept, and warnings work as for [Claude Code](#plan-limits-claude-code). There is no HTTP route to report them: `POST /api/limits/codex` is 404. A reading replaces the last one, also when it is lower (reset credits can do that inside a window). TaskHub looks for `codex.exe` on `PATH`, then inside the npm package next to `codex.cmd`.

`codex app-server` is documented by OpenAI as experimental. If Codex is not found, not logged in or answers differently, the log says so (error code only), the last numbers stay with their age shown. While the switch is on, `limits_problem` in `GET /api/tasks` says why the last read failed: `{codex: "not_found" | "login" | "failed"}`; the panel shows it as a line only when there are no numbers.

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
Use the full path (`%LOCALAPPDATA%\Programs\TaskHub\taskhub.exe`) if `taskhub` is not on PATH for Codex. Codex runs the command in PowerShell, so a path with a space needs `& "C:\my tools\taskhub.exe" hook codex` (TaskHub writes this itself when the path has no short name). New hooks must be
trusted once in Codex (`/hooks`). Do **not** replace an existing `notify = [...]` in `config.toml` (the Codex app uses it);
`taskhub hook codex` also accepts the legacy `notify` payload as its last argument if you need it.

## MCP server
`taskhub mcp [--source NAME]` (`notify` and `task_finish` also send `source` and `agent_cwd`, its working folder, for the `Stop` rule in [Claude Code hooks](#claude-code-hooks); its instructions ask the agent not to notify just to say it is done while TaskHub's hooks are on) is a stdio [MCP](https://modelcontextprotocol.io) server for AI agents with the tools
`notify`, `task_start`, `task_update`, `task_finish`, `task_commands` (button presses / input values, acknowledged on read),
`task_wait` (blocks up to 50 s until a task finishes - instead of polling with sleep; call again while it runs)
and `task_list`. It starts the hub on the first tool call. Calls run side by side (a waiting `task_wait` does
not hold other requests), `notifications/cancelled` stops a call (no answer for it), and the server ends when stdin
closes or the program that started it exits. Buttons in `task_start` are always `signal` controls with `id`, `label`, `style`, `confirm`, `hint`, `input`, `toast` and `states`; `kind`, `command`, `target` and the rest are dropped, so what an agent writes doesn't decide what the hub runs when you press it.
Enable it in the setup dialog / Settings → *AI agents*, or by hand:
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
