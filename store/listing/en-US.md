# en-US

## Description

TaskHub collects the long-running jobs on your PC in one place and lets you control them: video exports, renders, builds, backups, model training, CI runs, cloud GPU machines, and the scripts that AI coding agents start for you.

How it works
A program or script reports its progress to TaskHub through a local HTTP API on 127.0.0.1, the taskhub command, or the included Python client. TaskHub then shows:
• a tray icon with a progress ring and the number of active tasks;
• a panel with each task's progress, ETA, status, log and the widgets the task asks for: line, area and bar charts, gauges, stats, indicators, tables;
• the task's own controls: buttons, number, text and list inputs, pause, stop and kill. What you choose goes back to the task;
• Windows notifications when a task finishes, fails or stops reporting, with the task's buttons if it asks for them.

"taskhub run -- <command>" wraps any command line: it streams the output into the task log, reads progress from it and adds Stop and Kill buttons.

Settings
The ⚙ button in the panel (or Settings in the tray menu) opens the settings beside the panel, with tabs for appearance, notifications and Telegram, connectors, AI agents, autostart and the API reference. The tray menu keeps the quick actions: open the panel, mute, settings, support, exit.

Claude Code and Codex
In Settings → AI agents you can connect TaskHub to Claude Code and Codex. The agent gets TaskHub tools (MCP) to report its long jobs, and you get a notification when the agent finishes a reply or needs your permission. TaskHub adds its entries to the agents' settings files and removes them when you turn the option off. You can also turn on plan limits: how much of your Claude Code and Codex plan limits is used, in the panel, with warnings at 75% and 90%.

Connectors (optional, off by default)
• GitHub Actions: the workflow runs of the repositories you list, as tasks: progress by jobs and steps, the failed step and the end of its log, cancel and re-run buttons.
• Runpod and Vast.ai: your GPU machines as tasks: GPU, price per hour, spent so far, uptime, SSH, load charts, a "GPU idle" warning, balance and runway with a low-balance warning, stop / start / restart (delete only in the panel, after typing the machine's name).
• AWS: EC2 instances, SageMaker training jobs and AWS Batch jobs as tasks, this month's cost and forecast, and AWS Budgets, each behind its own switch. Read-only. Cost Explorer is billed by AWS ($0.01 per request, about $1.80 a month at most). This connector was built from the AWS documentation and tested against a simulator, not yet on a real AWS account.
Each connector works only after you turn it on and enter your own API key. The key is kept only in Windows Credential Manager, and the connector talks only to its own service's API (api.github.com, api.runpod.io, console.vast.ai, *.amazonaws.com).

Balance guard (optional, off until you turn it on)
For Runpod and Vast.ai: a warning at your own balance threshold, and at the stop threshold the running machines are stopped after a notification with Stop now and Snooze. It only stops machines and never deletes them; if the balance can't be read, it stops nothing.

Telegram (optional)
Connect your own Telegram bot to get the same notifications on your phone, a live progress message per task, and press the task's buttons from there. The bot token is kept in Windows Credential Manager, and messages go from your PC straight to Telegram's Bot API.

Privacy
No account, no telemetry. The API listens on 127.0.0.1 only, and your tasks and settings stay in %APPDATA%\TaskHub. Nothing leaves your PC unless you turn on Telegram, a connector or Codex plan limits.

Good to know
• Updates come from the Microsoft Store.
• Autostart can be turned off in Settings → System or in Windows Settings → Apps → Startup.
• The API reference opens from the settings page.
• Requires Windows 10 version 2004 or later (x64) and the Microsoft Edge WebView2 Runtime, which current Windows already includes.
• Interface languages: English, Ukrainian, Spanish, German, French, Italian, Portuguese, Polish, Chinese (Simplified), Japanese.

## What's new in this version

TaskHub 3.1
• AWS connector, off by default and read-only: EC2 instances, SageMaker and Batch jobs, this month's cost, Budgets.
• Plan limits for Claude Code and Codex, off until you turn them on: how much is used, with warnings at 75% and 90%.
• Settings open beside the panel, with tabs.
• Notifications from Claude Code and Codex are offered in the panel when an agent is found, and there are no more two notifications for one thing.
• A clearer local log: notification text and task names are written only at debug level.
• Runs on .NET 10. The pinned panel keeps its edge of the screen.
• Fixed: the Vast.ai balance; a Telegram button from before a restart could run a different task's button.

## Product features

- Tray icon with a progress ring and the number of active tasks
- Panel with progress, ETA, log, charts, gauges, stats and tables for every task
- Buttons and inputs declared by the task itself: pause, stop, change a parameter, kill
- Windows notifications on finish, failure and stall, with the task's buttons
- Local HTTP API, taskhub command line and Python client; "taskhub run" wraps any command
- Settings beside the panel (⚙), with tabs; quick actions in the tray menu
- Claude Code and Codex: TaskHub tools (MCP) and notifications, turned on in Settings
- Optional plan limits for Claude Code and Codex: how much is used, warnings at 75% and 90%
- Optional GitHub Actions connector: workflow runs as tasks, with cancel and re-run
- Optional Runpod and Vast.ai connectors: GPU machines, cost, balance, GPU idle warning, stop and start
- Optional AWS connector, read-only: EC2 instances, SageMaker and Batch jobs, cost, Budgets
- Optional balance guard: warns at low balance and stops (never deletes) the running machines
- Optional Telegram bot: notifications, progress and the task's buttons on your phone
- No account, no telemetry; API keys only in Windows Credential Manager; the API listens on 127.0.0.1 only
- 10 interface languages

## Short description

Watch and control long-running jobs on your PC: progress, charts, notifications and the task's own buttons. Works with any script through a local API or the taskhub command, with Claude Code and Codex, and optionally with GitHub Actions, Runpod, Vast.ai and AWS.

## Keywords

- task monitor
- background jobs
- progress notifications
- long-running scripts
- Claude Code
- Codex
- MCP

## Copyright and trademark info

© 2026 FelixMUrphy

## Developed by

FelixMUrphy
