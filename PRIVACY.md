# TaskHub privacy policy

TaskHub runs on your PC and has no servers, accounts, analytics, telemetry or ads. Its developer receives no data from
it.

## What stays on your PC
- Tasks, their progress, logs and charts, and your settings are kept in `%APPDATA%\TaskHub`.
- TaskHub keeps a diagnostic log there, `taskhub.log`, at most about 8 MB, with no automatic sending. It holds the version, Windows, .NET and WebView2 versions, the language, monitors, the port, the
  data and program folders, task states and ids (a hash if the id comes from a job's or instance's name), machine ids, and errors, whose text can contain a file path, a repository name or what a service replied. From TaskHub 3.1 notification text, task
  names and account balances are written only if you set `"log_level": "debug"` in `config.json` (a beta build does this by itself, keeps up to 80 MB, and the command-line tool adds `cli.log`, at most about 2 MB,
  also written at that level). From TaskHub 3.1, before writing, TaskHub removes what looks like a token or key, and when it starts at the default level it deletes a log with those detailed lines, `cli.log` and a beta build's extra log files. Look through a log before you share it with anyone.
- Programs on the same PC talk to TaskHub over a local HTTP API on `127.0.0.1`, which other computers cannot reach.
- If you turn on the Claude Code or Codex connection, TaskHub adds its entries to those programs' settings files in your
  user profile and removes them when you turn the connection off. The version installed from GitHub also removes them
  when it is uninstalled. Windows does not let the Microsoft Store version run anything on uninstall, so it points the
  agents at a small copy of its `taskhub` command in `%LOCALAPPDATA%\TaskHub\bin`; the next time an agent starts that
  copy after TaskHub was removed, the copy removes TaskHub's entries. To remove them right away, turn the connections
  off before you uninstall: tray menu → *AI agents* (from TaskHub 2.9: Settings in the panel → *AI agents*).
- From TaskHub 3.1, to offer those notifications, TaskHub looks on your PC for signs that Claude Code or Codex is used (their settings
  folders and files, the programs on your `PATH`) and remembers your answer and which agents it told you about in its own `config.json`. It adds nothing
  to their settings until you press *Connect*. With the connection on, a notification shows the project folder and the start of the agent's reply. Like
  every notification it goes to Telegram if you have connected Telegram.
- From TaskHub 3.1, TaskHub's MCP server tells TaskHub which folder it runs in, on your PC only and not stored, to avoid a second notification for the same thing.

## What leaves your PC, and only if you turn it on
- **Plan limits of Claude Code** (TaskHub 3.1 and later, off by default). If you turn this on in Settings → AI agents, TaskHub adds a
  status line command to `~/.claude/settings.json`. Your own status line, if you have one, keeps running unchanged, and a copy of its entry is kept in
  `~/.claude/statusline.taskhub.json` until you turn this off. TaskHub's command hands everything Claude Code gives a status line to your own status line, in memory, and passes only the used percentages and reset times of the 5-hour and
  weekly limits to the TaskHub app, over the local connection. They are stored in `plan_limits.json` in TaskHub's data folder with the time of each reading and which warnings were
  given, and deleted when you turn this off. A warning contains the percentage and the reset time and is shown like other notifications, including Telegram when that is on.
  TaskHub doesn't read Claude Code's account tokens for this.
- **Plan limits of Codex** (TaskHub 3.1 and later, off by default). If you turn this on in Settings → AI agents, TaskHub starts Codex on your PC for about a second once when you turn it on and then every 10 minutes while Codex is running, and asks it for the limits.
  Codex asks OpenAI for them with its own login and adds to its own logs and caches, as it does when you use it; no model request is made and TaskHub doesn't read Codex's account tokens. TaskHub gives Codex only its own name and version, which Codex may pass on to OpenAI. TaskHub keeps only the used percentages and reset times, in `plan_limits.json` with the time of each reading and which warnings were given, and deletes them when you turn this off. Warnings work as for Claude Code above.
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
  From 2.9.4, two more switches (off by default) also ask `api.github.com`, with the same token, for the pull requests
  waiting for your review and the open Dependabot pull requests of the listed repositories; their titles, numbers,
  repository and author names are shown as tasks (and in notifications, including Telegram when that is on). Nothing is
  written to GitHub.
- **Runpod connector** (TaskHub 2.9 and later; off by default, earlier versions don't have it). If you paste a Runpod API
  key and turn it on, TaskHub asks `api.runpod.io` with that key (in the `Authorization` header) for your pods and their
  live load, your pod billing of the last 30 days and (GraphQL, can be turned off) your balance, spend per hour, spend
  limit and billing mode (prepaid / post-paid) and, from 2.9.1, the size of the network volumes your pods use (for the
  storage-cost estimate after an auto-stop), and shows them as tasks; nothing about your PC or your other tasks is sent. To find out whether the key may run
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
- **AWS connector** (TaskHub 3.1 and later; off by default, earlier versions don't have it). If you paste an AWS access key and
  turn it on, TaskHub signs requests with it (the secret itself is never sent) and asks `ec2.<region>.amazonaws.com` for your instances in
  the Regions you watch, and `ec2.us-east-1.amazonaws.com` for the list of Regions, also when you don't watch that one. Each further switch, off by default, adds its hosts: SageMaker training jobs (`api.sagemaker.<region>.amazonaws.com`),
  AWS Batch jobs (`batch.<region>.amazonaws.com`), this month's cost and forecast (`ce.us-east-1.amazonaws.com`, which AWS charges for) and
  AWS Budgets (`budgets.amazonaws.com`, plus `sts.amazonaws.com` for the account number, kept in memory only). Instance and job names and ids, instance types, Regions, Batch queue names, states and
  times, failure reasons (with ARNs and account numbers removed) become tasks: kept in TaskHub's data folder and shown
  in notifications, including Telegram when that is on. Budget names and amounts are shown in the settings; the name of a budget over its limit also appears in a
  notification (including Telegram when that is on). The last cost figures and the names of budgets over their limit stay in `config.json`, with the ids of the instances you marked as needed.
  The key is kept in the Windows Credential Manager, not in TaskHub's files. Nothing is changed in your AWS account. AWS's own privacy policy applies to these
  requests. Turning the connector off stops them (the key stays saved); *Remove* deletes the key.
- **Balance guard** (TaskHub 2.9 and later; off by default). If you turn it on for Runpod or Vast.ai, TaskHub uses the
  balance it already reads from that service; at your stop threshold it sends that service the same *stop* request as the
  Stop button (never delete), for the running machines you did not mark *Never auto-stop*. Auto-stop on GPU idle
  (TaskHub 2.9.3 and later; off by default, also without the balance guard) sends the same *stop* request for a machine
  whose GPU stayed idle as long as you set, judged from the load TaskHub already reads; its log records the idle minutes
  instead of a threshold, and its pending stop is not saved to disk. Its notifications — the balance,
  the spend per hour, the time left, the names of the machines it stops and what their storage costs while stopped — are
  shown on Windows and, if you connected Telegram, sent to your Telegram chat like other notifications. A record of each stop
  (time, provider, machine id and name, balance, threshold, spend, result, storage estimate) is kept only on your PC in
  `%APPDATA%\TaskHub\guard_log.json` (at most 200 entries); a stop that is still pending (its deadline and machine ids) is
  kept in `guard_runpod.json` / `guard_vastai.json` in the same folder so it survives a restart. Both are deleted with that
  folder.
- **Update check** (only in the version installed from GitHub, not in the Microsoft Store version). Once a day TaskHub
  asks `api.github.com` for the latest release number; from TaskHub 2.9.5 also when you open its settings page and the
  last check is older than 15 minutes, and after *Update to X* (the same request, so the offered version is current). It sends no personal data, and you can turn the check off in the
  tray menu (*Updates*; from TaskHub 2.9: Settings in the panel → *System*).

*Support TaskHub* in the tray menu, the panel and its settings only opens GitHub Sponsors in your browser; TaskHub itself sends
nothing there.

## Removing your data
Uninstalling TaskHub removes the program; your tasks and settings stay in `%APPDATA%\TaskHub`. To delete them as well,
delete that folder (or uninstall the GitHub version with *Also delete my data* / `TaskHubHost.exe --uninstall --purge`).
The Telegram bot token and the GitHub, Runpod, Vast.ai and AWS keys stay in the Windows Credential Manager after a plain
uninstall, so a reinstall finds them; *Also delete my data* / `--purge` deletes them too (TaskHub 2.9 and later), or
remove the `TaskHub/...` entries in Control Panel → Credential Manager → Windows Credentials, or disconnect Telegram or press
*Remove* on the connector in TaskHub's settings before uninstalling. Uninstalling the Microsoft Store version doesn't delete them either.
The Microsoft Store version also leaves its `taskhub` copy in `%LOCALAPPDATA%\TaskHub`, which you can delete.

## Contact
Questions: open an issue at https://github.com/FelixMUrphy/TaskHub/issues.
