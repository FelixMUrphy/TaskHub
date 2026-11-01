# Changelog

What changed in each release of TaskHub. The release page on GitHub shows the section of its version (CI takes it
from here), followed by the list of commits.

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
