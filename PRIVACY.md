# TaskHub privacy policy

TaskHub runs on your PC and has no servers, accounts, analytics, telemetry or ads. Its developer receives no data from
it.

## What stays on your PC
- Tasks, their progress, logs and charts, and your settings are kept in `%APPDATA%\TaskHub`.
- Programs on the same PC talk to TaskHub over a local HTTP API on `127.0.0.1`, which other computers cannot reach.
- If you turn on the Claude Code or Codex connection, TaskHub adds its entries to those programs' settings files in your
  user profile and removes them when you turn the connection off. The version installed from GitHub also removes them
  when it is uninstalled. Windows does not let the Microsoft Store version run anything on uninstall, so it points the
  agents at a small copy of its `taskhub` command in `%LOCALAPPDATA%\TaskHub\bin`; the next time an agent starts that
  copy after TaskHub was removed, the copy removes TaskHub's entries. To remove them right away, turn the connections
  off in the tray menu (*AI agents*) before you uninstall.

## What leaves your PC, and only if you turn it on
- **Telegram** (off by default). If you connect a Telegram bot, TaskHub sends your notifications, task progress and
  charts from your PC straight to Telegram's Bot API, to the chat you linked, and receives your button presses and
  replies from it. The bot token and chat ID are stored in `%APPDATA%\TaskHub\config.json`. Telegram's own privacy policy
  applies to these messages. Disconnecting the bot in TaskHub stops this.
- **Update check** (only in the version installed from GitHub, not in the Microsoft Store version). Once a day TaskHub
  asks `api.github.com` for the latest release number. It sends no personal data, and you can turn the check off in the
  tray menu (*Updates*).

*Support TaskHub* in the tray menu and the panel only opens GitHub Sponsors in your browser; TaskHub itself sends
nothing there.

## Removing your data
Uninstalling TaskHub removes the program; your tasks and settings stay in `%APPDATA%\TaskHub`. To delete them as well,
delete that folder (or uninstall the GitHub version with `TaskHubHost.exe --uninstall --purge`). The Microsoft Store
version also leaves its `taskhub` copy in `%LOCALAPPDATA%\TaskHub`, which you can delete.

## Contact
Questions: open an issue at https://github.com/FelixMUrphy/TaskHub/issues.
