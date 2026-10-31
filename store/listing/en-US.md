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

Settings in the panel
Everything is set up on one page: the ⚙ button in the panel (or Settings in the tray menu) opens appearance, notifications and Telegram, connectors, AI agents, autostart and the API reference. The tray menu keeps the quick actions: open the panel, mute, settings, support, exit.

Claude Code and Codex
In Settings → AI agents you can connect TaskHub to Claude Code and Codex. The agent gets TaskHub tools (MCP) to report its long jobs, and you get a notification when the agent finishes a reply or needs your permission. TaskHub adds its entries to the agents' settings files and removes them when you turn the option off.

Connectors (optional, off by default)
• GitHub Actions: the workflow runs of the repositories you list, as tasks: progress by jobs and steps, the failed step and the end of its log, cancel and re-run buttons.
• Runpod and Vast.ai: your GPU machines as tasks: GPU, price per hour, spent so far, uptime, SSH, load charts, a "GPU idle" warning, balance and runway with a low-balance warning, stop / start / restart (delete only in the panel, after typing the machine's name).
Each connector works only after you turn it on and enter your own API key. The key is kept only in Windows Credential Manager, and the connector talks only to its own service's API (api.github.com, api.runpod.io, console.vast.ai).

Balance guard (optional, off until you turn it on)
For Runpod and Vast.ai: a warning at your own balance threshold, and at the stop threshold the running machines are stopped after a notification with Stop now and Snooze. It only stops machines and never deletes them; if the balance can't be read, it stops nothing.

Telegram (optional)
Connect your own Telegram bot to get the same notifications on your phone, a live progress message per task, and press the task's buttons from there. The bot token is kept in Windows Credential Manager, and messages go from your PC straight to Telegram's Bot API.

Privacy
No account, no telemetry. The API listens on 127.0.0.1 only, and your tasks and settings stay in %APPDATA%\TaskHub. Nothing leaves your PC unless you turn on Telegram or a connector.

Good to know
• Updates come from the Microsoft Store.
• Autostart can be turned off in Settings → System or in Windows Settings → Apps → Startup.
• The API reference opens from the settings page.
• Requires Windows 10 version 2004 or later (x64) and the Microsoft Edge WebView2 Runtime, which current Windows already includes.
• Interface languages: English, Ukrainian, Spanish, German, French, Italian, Portuguese, Polish, Chinese (Simplified), Japanese.

## What's new in this version

TaskHub 2.9
• Settings page in the panel (⚙): everything in one place; the tray menu keeps the quick actions, in the Windows 11 style.
• New connectors, off by default: GitHub Actions (workflow runs with cancel and re-run), Runpod and Vast.ai (GPU machines, cost, balance, GPU idle warning, stop and start). Your API keys are kept only in Windows Credential Manager.
• Balance guard for Runpod and Vast.ai, off until you turn it on: a warning at your threshold, and at the stop threshold the running machines are stopped, never deleted.
• Telegram: the bot token moves to Windows Credential Manager; TaskHub keeps to Telegram's message rate instead of dropping messages.
• Card transparency setting; file and folder links are shown in Explorer, never run.
• 2.9.1: amounts of money with two decimals, Runpod network volume storage cost.
• Now requires Windows 10 version 2004 or later.

## Product features

- Tray icon with a progress ring and the number of active tasks
- Panel with progress, ETA, log, charts, gauges, stats and tables for every task
- Buttons and inputs declared by the task itself: pause, stop, change a parameter, kill
- Windows notifications on finish, failure and stall, with the task's buttons
- Local HTTP API, taskhub command line and Python client; "taskhub run" wraps any command
- All settings on one page in the panel (⚙); quick actions in the tray menu
- Claude Code and Codex: TaskHub tools (MCP) and notifications, turned on in Settings
- Optional GitHub Actions connector: workflow runs as tasks, with cancel and re-run
- Optional Runpod and Vast.ai connectors: GPU machines, cost, balance, GPU idle warning, stop and start
- Optional balance guard: warns at low balance and stops (never deletes) the running machines
- Optional Telegram bot: notifications, progress and the task's buttons on your phone
- No account, no telemetry; API keys only in Windows Credential Manager; the API listens on 127.0.0.1 only
- 10 interface languages

## Short description

Watch and control long-running jobs on your PC: progress, charts, notifications and the task's own buttons. Works with any script through a local API or the taskhub command, with Claude Code and Codex, and optionally with GitHub Actions, Runpod and Vast.ai.

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
