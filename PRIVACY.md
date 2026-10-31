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
  off before you uninstall: tray menu → *AI agents* (from TaskHub 2.9: Settings in the panel → *AI agents*).

## What leaves your PC, and only if you turn it on
- **Telegram** (off by default). If you connect a Telegram bot, TaskHub sends your notifications, task progress and
  charts from your PC straight to Telegram's Bot API, to the chat you linked, and receives your button presses and
  replies from it. The chat ID is stored in `%APPDATA%\TaskHub\config.json`; the bot token is kept in the Windows Credential
  Manager of your user account (TaskHub 2.9 and later; earlier versions keep it in `config.json`, and 2.9 moves it on its
  first start). If the Credential Manager is unavailable in your Windows session, the token stays in `config.json` and
  TaskHub's settings show a warning (it moves the token on a later start once the Credential Manager works). Telegram's own privacy policy
  applies to these messages. Disconnecting the bot in TaskHub stops this.
- **GitHub Actions connector** (TaskHub 2.9 and later; off by default, earlier versions don't have it). If you turn it
  on and list repositories, TaskHub asks `api.github.com` about the workflow runs of those repositories, with the GitHub
  token you give it, and shows them as tasks; its buttons can cancel or re-run a run on GitHub when you press them. If you
  also turn on the log option, the end of a failed job's log is downloaded from GitHub's log storage through the one-time
  link GitHub returns, without the token. The token is kept in the Windows Credential Manager of your user account, not in
  TaskHub's files (or, if you choose so, TaskHub uses the token the Git Credential Manager already keeps). GitHub's own
  privacy policy applies to these requests. Turning the connector off stops them (the token stays saved); *Remove* deletes the token.
- **Runpod connector** (TaskHub 2.9 and later; off by default, earlier versions don't have it). If you paste a Runpod API
  key and turn it on, TaskHub asks `api.runpod.io` with that key (in the `Authorization` header) for your pods and their
  live load, your pod billing of the last 30 days and (GraphQL, can be turned off) your balance, spend per hour and spend
  limit, and shows them as tasks; nothing about your PC or your other tasks is sent. To find out whether the key may run
  actions, TaskHub sends one harmless request a day — "stop" for a pod that does not exist. Stop, start, restart and delete
  are sent only when you press the button and confirm. The key is kept in the Windows Credential Manager of your user
  account, not in TaskHub's files. Runpod's own privacy policy applies to these requests. Turning the connector off stops
  them (the key stays saved); *Remove* deletes the key.
- **Vast.ai connector** (TaskHub 2.9 and later; off by default, earlier versions don't have it). If you paste a Vast.ai API
  key and turn it on, TaskHub asks `console.vast.ai` with that key (in the `Authorization` header) for your instances and
  their load, your instance charges of the last 30 days and your account's balance and credit (that answer also contains
  your account details; TaskHub reads only the balance / credit fields and keeps nothing else), and shows them as tasks;
  nothing about your PC or your other tasks is sent. To find out whether the key may run actions, TaskHub sends one harmless
  request a day — setting a label on an instance that does not exist. Stop, start, reboot and destroy are sent only when you
  press the button and confirm. The key is kept in the Windows Credential Manager of your user account, not in TaskHub's
  files. Vast.ai's own privacy policy applies to these requests. Turning the connector off stops them (the key stays saved);
  *Remove* deletes the key.
- **Update check** (only in the version installed from GitHub, not in the Microsoft Store version). Once a day TaskHub
  asks `api.github.com` for the latest release number. It sends no personal data, and you can turn the check off in the
  tray menu (*Updates*; from TaskHub 2.9: Settings in the panel → *System*).

*Support TaskHub* in the tray menu, the panel and its settings only opens GitHub Sponsors in your browser; TaskHub itself sends
nothing there.

## Removing your data
Uninstalling TaskHub removes the program; your tasks and settings stay in `%APPDATA%\TaskHub`. To delete them as well,
delete that folder (or uninstall the GitHub version with *Also delete my data* / `TaskHubHost.exe --uninstall --purge`).
The Telegram bot token and the GitHub token (TaskHub 2.9 and later) stay in the Windows Credential Manager after a plain
uninstall, so a reinstall finds them; *Also delete my data* / `--purge` deletes them too (TaskHub 2.9 and later), or
remove the `TaskHub/...` entries in Control Panel → Credential Manager → Windows Credentials, or disconnect Telegram /
GitHub in TaskHub's settings before uninstalling. Uninstalling the Microsoft Store version doesn't delete them either.
The Microsoft Store version also leaves its `taskhub` copy in `%LOCALAPPDATA%\TaskHub`, which you can delete.

## Contact
Questions: open an issue at https://github.com/FelixMUrphy/TaskHub/issues.
