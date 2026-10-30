# en-US

## Description

TaskHub collects the long-running jobs on your PC in one place and lets you control them: video exports, renders, builds, backups, model training, and the scripts that AI coding agents start for you.

How it works
A program or script reports its progress to TaskHub through a local HTTP API on 127.0.0.1, the taskhub command, or the included Python client. TaskHub then shows:
• a tray icon with a progress ring and the number of active tasks;
• a panel with each task's progress, ETA, status, log and the widgets the task asks for: line, area and bar charts, gauges, stats, indicators, tables;
• the task's own controls: buttons, number, text and list inputs, pause, stop and kill. What you choose goes back to the task;
• Windows notifications when a task finishes, fails or stops reporting, with the task's buttons if it asks for them.

"taskhub run -- <command>" wraps any command line: it streams the output into the task log, reads progress from it and adds Stop and Kill buttons.

Claude Code and Codex
In the tray menu (AI agents) you can connect TaskHub to Claude Code and Codex. The agent gets TaskHub tools (MCP) to report its long jobs, and you get a notification when the agent finishes a reply or needs your permission. TaskHub adds its entries to the agents' settings files and removes them when you turn the option off.

Telegram (optional)
Connect your own Telegram bot to get the same notifications on your phone and press the task's buttons from there. Messages go from your PC straight to Telegram's Bot API.

Privacy
No account, no cloud service, no telemetry. The API listens on 127.0.0.1 only, and your tasks and settings stay in %APPDATA%\TaskHub.

Good to know
• Updates come from the Microsoft Store.
• Autostart can be turned off in the tray menu or in Settings → Apps → Startup.
• The API reference opens from the tray menu (API reference).
• Requires Windows 10 version 1809 or later (x64) and the Microsoft Edge WebView2 Runtime, which current Windows already includes.
• Interface languages: English, Ukrainian, Spanish, German, French, Italian, Portuguese, Polish, Chinese (Simplified), Japanese.

## Product features

- Tray icon with a progress ring and the number of active tasks
- Panel with progress, ETA, log, charts, gauges, stats and tables for every task
- Buttons and inputs declared by the task itself: pause, stop, change a parameter, kill
- Windows notifications on finish, failure and stall, with the task's buttons
- Local HTTP API, taskhub command line and Python client; "taskhub run" wraps any command
- Claude Code and Codex: TaskHub tools (MCP) and notifications, turned on from the tray menu
- Optional Telegram bot: notifications, progress and the task's buttons on your phone
- No account, no cloud, no telemetry; the API listens on 127.0.0.1 only
- 10 interface languages

## Short description

Watch and control long-running jobs on your PC: progress, charts, notifications and the task's own buttons. Works with any script through a local API or the taskhub command, and with Claude Code and Codex.

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
