# Restricted capabilities and notes for certification

Paste into Partner Center → the submission → Submission options → **Notes for certification** (and, if Partner Center
asks separately, the two capability justifications into the restricted capabilities field).

## runFullTrust

TaskHub is a desktop tray application (WinForms + WebView2, .NET 8). It runs a local HTTP API on 127.0.0.1:47800 that
the user's own scripts and AI coding agents (Claude Code, Codex) use to report long-running jobs, shows a tray icon,
Windows notifications and a task panel, and acts on the user's commands for those jobs: it signals or kills the
reporting process (the "Stop" and "Kill" buttons) and, when a task declares one, runs the command line the task
attached to a button. The "taskhub" command line tool (app execution alias taskhub.exe) is the client for that API and
the MCP server that AI agents start. None of this works inside an AppContainer, so the app needs full trust.

## unvirtualizedResources

TaskHub is also distributed as a classic per-user installer (GitHub releases). Both editions use the same data folder
(%APPDATA%\TaskHub) and the same settings files of Claude Code and Codex in the user profile, and only one of them may
be installed at a time. When the Store edition starts and finds the installer edition, it asks the user whether to
switch over. If the user agrees, it removes the installer edition: its files in %LOCALAPPDATA%\Programs\TaskHub, its
autostart value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run, its taskhub:// handler in
HKCU\Software\Classes\taskhub, its Apps & features entry in
HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskHub and its folder in the user PATH. With write
virtualization these changes would only be hidden from the package and the entries would stay behind for the user and
for Windows, and the user's tasks and settings would be split between two copies of the data folder. The package
writes nowhere else outside its own install folder except %APPDATA%\TaskHub and, only when the user turns the AI agent
connections on, the Claude Code and Codex settings files (%USERPROFILE%\.claude.json, %USERPROFILE%\.claude\settings.json,
%USERPROFILE%\.codex\config.toml, %USERPROFILE%\.codex\hooks.json) and a copy of the package's own taskhub.exe in
%LOCALAPPDATA%\TaskHub\bin that those settings point to. Windows runs no app code on uninstall, so this copy is what
cleans up: when an agent starts it after the package was removed, it removes TaskHub's entries from the agents'
settings files, instead of leaving the agents with a command that no longer exists.

## How to test

No account or sign-in is needed. After the first start TaskHub sits in the notification area (no main window); click
its icon to open the panel. In PowerShell:

    taskhub run --name "Demo" -- ping -n 30 127.0.0.1

shows a task with progress and a log in the panel and a notification when it ends. Or step by step:

    $id = taskhub register --name "Demo" --total 10
    taskhub update $id --current 5
    taskhub finish $id

Telegram (tray menu → Telegram…) is optional and needs the tester's own bot token from @BotFather.

## Support link

The "Support TaskHub" item (tray menu, and the heart button in the panel) opens https://github.com/sponsors/FelixMUrphy in
the browser. Sponsorship is voluntary, is handled entirely by GitHub, and unlocks nothing in the app (policy 10.8.2).
