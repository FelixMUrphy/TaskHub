# Changelog

What changed in each release of TaskHub. The release page on GitHub shows the section of its version (CI takes it
from here), followed by the list of commits.

## Unreleased

- New: Discord. Your notifications, a live progress message, charts and the tasks' buttons as direct messages from your own bot, off until you connect it. Built from Discord's documentation, tested against a simulator and checked with a real bot.

- Safer agent notifications: before sending anything, the `taskhub` command checks that it is really talking to TaskHub. With TaskHub closed, a program of another Windows account listening on its port gets neither an agent's message nor the token.
- Telegram: a bot you blocked no longer shows as connected; the page says Telegram refuses to deliver. A bot's name no longer stays empty after one failed request at start.
- GitHub Actions: a run with more than 100 jobs is counted in full, and a failed job past the first 100 is found. Borrowing Git's credentials never opens a password window, even if Git has an askpass program set. Over the local API, nothing can change which run a task's Cancel and Re-run act on.
- `taskhub run`: Ctrl+C from the panel ends the task as cancelled, not failed. When a command goes through `cmd`, an argument with `&` stays one argument instead of starting a second command.
- Shell buttons: their output in the task log is readable in any language, whether the command writes UTF-8 or the console's code page.
- The balance guard and idle stop of Runpod and Vast.ai change only in Settings, no longer through the local API (403): they stop your machines, so a script or an agent can't turn them on or off. Reading them still works.
- Windows notifications: a list with more than 5 options keeps its current value among the 5 shown, so the button can't send another one.
- Install: a quiet install no longer carries the downloaded file's Mark of the Web. Uninstall removes this copy's autostart entries for other ports. The Apps list shows the install date and a link; `taskhub:` links have an icon.
- A TaskHub that starts while another program answers on its port no longer sends that program its token. A command under `taskhub run` makes the same check as `taskhub` itself.

## 3.1.0 — 2026-11-03

- New connector: AWS. EC2 instances, SageMaker training jobs and AWS Batch jobs as tasks, this month's cost and forecast, AWS Budgets, each behind its own switch. Off until you turn it on, and read-only. Built from AWS's documentation and tested against a simulator, not yet on a real account. Cost Explorer is billed by AWS ($0.01 per request; about $1.80 a month). That switch goes on only in Settings, not through the local API.
- Settings open beside the panel, with icon tabs. While the panel is pinned they close only with their own button.
- Plan limits for Claude Code, off until you turn them on: how much of the 5-hour and weekly limits is used, in the panel, yellow with a warning from 75%, red with a warning from 90%. Your own status line keeps working unchanged. Also by script: `TaskHubHost.exe --integrations claude-limits=on` (Codex: with TaskHub closed).
- Plan limits for Codex too, off until you turn them on: TaskHub asks Codex every 10 minutes while it is running, with no model requests. Each row says how much is used. In the panel each agent's limits sit under its own name, and the bars take the Windows accent colour.
- The About page says in a few words what TaskHub does.
- Fixed: a button in a Telegram message sent before TaskHub restarted could run a button of a different task. Such a button now says it has expired.
- A path naming another machine in an agent's message is no longer looked up, so TaskHub makes no connection there and doesn't stall. A reset time no limit window can have counts as no reset time.
- On battery the panel and settings appear and go at once, without the slide, which could leave text running over the panel or onto the next monitor.
- Vast.ai: the balance shows again, and with it the low-balance warnings and the balance guard. Vast.ai had started redirecting the address TaskHub asked.
- A task's percentage now shares a row with its status, and the task counter is short (1 / 3), so the header no longer wraps.
- GitHub Actions: the bar follows the same time estimate as the ETA instead of standing still during a long step. A run past its usual duration says so.
- Notifications from Claude Code and Codex are offered in the panel when an agent is found and you haven't chosen yet. Nothing changes in the agent's settings until you press Connect. A switch left off in setup for an agent that isn't on the PC no longer counts as a no.
- No more two notifications for one thing: "response ready" is left out when the agent has just sent its own notification or finished a task from the same project. With the hooks on, TaskHub's MCP server also asks the agent not to notify just to say it is done.
- Safer Claude Code notifications: TaskHub's hook is now a command rather than a local web address, and no token stays in Claude Code's settings. With TaskHub closed, another program listening on its port could read the agent's last message and steer it. Existing entries are replaced on start; this needs a recent Claude Code.
- Codex notifications now work when TaskHub is installed in a folder with a space in its path on a drive without short names.
- A clearer local log: date and level on every line and what a problem report needs at the top. Notification text and task names are no longer written unless you set `"log_level": "debug"` in `config.json`, and anything that looks like a token or key is removed. At that level the command-line tool writes its own failures to `cli.log`. Back at the default level, TaskHub deletes on start what a beta build or that level left behind. Script errors in the panel now go to the log.
- GitHub Actions: a repository the token can't reach now gets one clear line in your language that says what to check, instead of two English and mixed messages.

## 3.0.0 — 2026-11-02

- TaskHub now runs on .NET 10 (long-term support until November 2028); .NET 8 support ends on 10 November 2026.
  The runtime is still inside the app, nothing to install.

## 2.9.7 — 2026-11-02

- The pinned panel keeps its edge of the screen: maximized windows make room for it and get the space back when you
  unpin or hide it. A new switch on the settings page (off by default) also moves or narrows the other windows on that
  screen, and puts them back on unpin.
- Fixed: after adding a repository on the GitHub settings page as a link (`https://github.com/owner/repo`), the text
  stayed in the field although the repository was added.
- API.md: an Errors section (error body and status codes), long-poll limits, how commands are delivered (once, on
  read) and acked, which progress field wins, the token rule (every request except `/api/health`) and where `config.json` is; what
  registering an existing `id` keeps, what counts against `stale`, the default control `kind`, connector and guard
  fields in one request.

## 2.9.6 — 2026-11-01

- GitHub Actions is set up right on the settings page (*Connectors* → *GitHub Actions*): access, repositories with their
  state, interval, notifications and pull requests, instead of a separate window.
- `taskhub mcp`: a waiting `task_wait` no longer holds the agent's other TaskHub calls (they waited up to 50 s); a call the
  agent cancels stops; the server ends together with the program that started it, so no orphaned `taskhub mcp`
  processes are left behind.
- Fixed: the spend guard (Runpod / Vast.ai) could hold its automatic stop for up to 10 minutes (or a day) when the key
  was saved while a check of its rights was still running; the check now runs again after the save.

## 2.9.5 — 2026-11-01

- Fixed: *Update to X* on the settings page could stay at an older version for up to a day after a newer one came out
  (the hub keeps running while the panel is hidden). The open settings page re-checks quietly when the last check is
  older than 15 minutes (only with automatic checks on), and *Update to X* always opens the newest release.

## 2.9.4 — 2026-11-01

- GitHub: **pull requests, read-only** (off by default): PRs waiting for your review and open Dependabot PRs of the
  watched repositories become tasks with a link and a notification; they end quietly when merged, closed or reviewed.
  Nothing is merged, approved or commented on.
- **Telegram** is set up right on the settings page (token, the t.me link with its QR code, the switches, Send test,
  New link, Disconnect) instead of a separate old-style window.
- Transparency sliders say when Windows draws no blur: energy saver on, or Transparency effects off in Windows settings
  (with a link to that setting); the hint comes and goes without a restart.
- Fixed: uninstalling the GitHub edition from Settings → Apps said "installed from the Microsoft Store" and removed nothing.
- Fixed: *Check for updates* said nothing when TaskHub was up to date; the answer now shows on the settings page.

## 2.9.3 — 2026-11-01

- Runpod / Vast.ai: **auto-stop on GPU idle** (off by default): *Stop when the GPU is idle for, min*. A machine idle that
  long gets the balance guard's announcement with *Stop now* / *Snooze*; GPU busy again cancels it; stop only, never
  delete; *Never auto-stop* machines are left alone. Works with the balance guard off. At the deadline the machine must
  have a recent idle reading (unknown load: not stopped).
- `taskhub run`, Blender preset fixes: Ctrl+C after some frames were saved is *cancelled* again, not *failed*; the printed
  resume command keeps a quoted path that ends with `\` intact and no longer changes the arguments after `--` (those
  belong to Python scripts). Video output (FFmpeg, "Append frame N") counts frames and *Stop after this frame* works for
  it; stereo views saved as separate files count as one frame; the memory-leak note Blender prints at exit no longer
  fails a good render; *Stop after this frame* now ends Blender when the next frame begins (pressed during the last
  frame: the render just finishes, *done*).

## 2.9.2 — 2026-11-01

- MCP: new tool `task_wait` — blocks up to 50 s until a task finishes, so an agent waits for a job instead of polling
  with sleep.
- The MCP server is also published as an MCP bundle (`taskhub-mcp-win-x64.mcpb`, a release asset) and listed in the
  MCP Registry as `io.github.FelixMUrphy/taskhub`; it needs the TaskHub app installed.
- `taskhub run -- blender -b …` reads Blender's render output: frame and samples, overall progress when the frame range
  is known, seconds-per-frame and memory charts, the last saved file. A render error fails the task (Blender itself
  exits with 0). *Stop after this frame* ends the render cleanly; after a stop or a crash the task and the console say
  how to resume (`-s` set to the next frame). `--preset blender` / `--preset none` choose it by hand.

## 2.9.1 — 2026-10-31

- Amounts of money are shown with two decimals everywhere ($0.34/h, ≈ $1.12).
- Runpod: a network volume's storage cost is included in the balance guard's "storage while stopped" estimate (its
  size is read from Runpod).
- Balance guard: neutral wording of the stop notification; the Runpod settings explain that its balance moves in
  steps, so the stop threshold needs some margin.

## 2.9.0 — 2026-10-31

- **Settings page in the panel** (⚙): appearance, notifications and Telegram, connectors, AI agents, autostart,
  updates and the data folder in one place. The tray menu keeps the quick actions, in the Windows 11 style.
- **Setup window**: installing and uninstalling show their progress and end with *Open TaskHub* (or an error page);
  *Also delete my data* on uninstall. `--install --quiet` works as before.
- **Connectors** (off until you turn them on; keys only in the Windows Credential Manager):
  - GitHub Actions — workflow runs of your repositories as tasks: progress by jobs and steps, ETA, the failed step
    and the end of its log, cancel / re-run.
  - Runpod and Vast.ai — your GPU machines as tasks: GPU, region, image, $/h, spent, uptime, SSH, load charts,
    *GPU idle* warning, balance and runway, stop / start / restart (delete only in the panel).
- **Balance guard** for Runpod and Vast.ai (off until you turn it on): a warning at your threshold, and at the stop
  threshold the running machines are stopped — never deleted — after a notification with *Stop now* / *Snooze*.
- **Telegram**: the bot token moves to the Windows Credential Manager; TaskHub keeps to Telegram's message rate
  instead of dropping messages.
- File and folder links are shown in Explorer, never run; shell controls get a typed value as plain text.
- Card transparency setting; notification bursts fold into one; the panel recovers by itself if WebView2 crashes.
- Microsoft Store edition now requires Windows 10 version 2004 or later.

## 2.8.0 — 2026-10-30

- Windows 11 panel: docked to the right edge, blurred see-through background, light / dark theme and accent colour
  from Windows, pin to keep it open, overlay scrollbar.

## Earlier

- 2.7 — Microsoft Store edition.
- 2.6.2 – 2.6.3 — daily update check; the installer detects the installed version.
- 2.5 – 2.6 — Telegram bridge: notifications, progress and the task's buttons on your phone.
- 2.0 — rewrite in C# / .NET 8: tray hub, native `taskhub` CLI and MCP server, WebView2 panel.
