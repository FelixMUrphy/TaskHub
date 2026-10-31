# Restricted capabilities and notes for certification

Partner Center → the submission → Submission options. **Notes for certification**: the short version below (the
MSI/EXE docs cap that field at 2,000 characters; the MSIX docs state no limit, so the full text also fits if accepted).
**Restricted capabilities** justification: the runFullTrust and unvirtualizedResources sections; Network access, How to
test and Support link go with them or into the notes if there is room.

## Short version (Notes for certification, under 2,000 characters)

TaskHub is a tray app (no main window; click the tray icon to open the panel). No account or sign-in is needed. Test in
PowerShell: taskhub run --name "Demo" -- ping -n 30 127.0.0.1 (a task with progress and log in the panel, a notification
when it ends). runFullTrust: desktop WinForms + WebView2 app with a local HTTP API on 127.0.0.1:47800, the taskhub.exe
CLI / MCP server alias, and Stop / Kill of the user's own reporting processes. unvirtualizedResources: the Store edition
offers to remove the user's classic per-user installer edition (HKCU Run / Classes / Uninstall entries, user PATH,
%LOCALAPPDATA%\Programs\TaskHub), shares %APPDATA%\TaskHub with it, and, only when the user turns AI agent connections
on, writes TaskHub's own entries to the Claude Code / Codex settings files in the user profile. Network: nothing by
default; Telegram (api.telegram.org) and the optional connectors GitHub Actions (api.github.com), Runpod (api.runpod.io)
and Vast.ai (console.vast.ai) connect only after the user turns them on and enters their own key, kept in Windows
Credential Manager. The optional balance guard only stops (never deletes) the user's cloud machines. Settings: the gear
button in the panel. "Support TaskHub" opens GitHub Sponsors in the browser; voluntary, unlocks nothing (10.8.2).
Details for each capability follow in the restricted capabilities justification.

## runFullTrust

TaskHub is a desktop tray application (WinForms + WebView2, .NET 8). It runs a local HTTP API on 127.0.0.1:47800 that
the user's own scripts and AI coding agents (Claude Code, Codex) use to report long-running jobs, shows a tray icon,
Windows notifications and a task panel, and acts on the user's commands for those jobs: it signals or kills the
reporting process (the "Stop" and "Kill" buttons) and, when a task declares one, runs the command line the task
attached to a button. When the user turns one on and enters a key, TaskHub's optional connectors (GitHub Actions,
Runpod, Vast.ai) show the user's own workflow runs or cloud GPU machines as tasks in the same panel and, on the user's
confirmed click, cancel / re-run a run or stop / start / restart a machine. The "taskhub" command line tool (app execution alias taskhub.exe) is the client for that API and
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

## Network access

The app makes no network connections of its own by default (the local API listens on 127.0.0.1 only; no telemetry, no
account, no in-app update check in the Store edition). Outbound HTTPS happens only for features the user turns on:
Telegram (api.telegram.org, with the user's own bot token) and the connectors: GitHub Actions (api.github.com; with the
optional log tail also GitHub's log storage through the one-time link GitHub returns, without the token), Runpod
(api.runpod.io) and Vast.ai (console.vast.ai). Each connector is off by default and talks only to its own allow-listed
hosts, and only after the user turns it on and enters an API key. The keys and the bot token are kept in the Windows
Credential Manager of the user's account, not in the app's files. The optional balance guard (Runpod, Vast.ai; off until
turned on) only stops the user's running machines at a threshold the user sets, after a notification; it never deletes
them.

## How to test

No account or sign-in is needed. After the first start TaskHub sits in the notification area (no main window); click
its icon to open the panel. In PowerShell:

    taskhub run --name "Demo" -- ping -n 30 127.0.0.1

shows a task with progress and a log in the panel and a notification when it ends. Or step by step:

    $id = taskhub register --name "Demo" --total 10
    taskhub update $id --current 5
    taskhub finish $id

All settings are on one page: the ⚙ button in the panel (or Settings in the tray menu). Telegram (Settings →
Notifications → Telegram) and the connectors (Settings → Connectors) are optional, off by default, and need the tester's
own bot token from @BotFather or the tester's own GitHub / Runpod / Vast.ai account and API key; nothing else in the app
depends on them.

## Support link

The "Support TaskHub" item (tray menu, the heart button in the panel and the settings page) opens https://github.com/sponsors/FelixMUrphy in
the browser. Sponsorship is voluntary, is handled entirely by GitHub, and unlocks nothing in the app (policy 10.8.2).
