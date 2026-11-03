# Connectors

A connector watches an outside service and shows its jobs as TaskHub tasks. Each one lives in its own folder:

| Path | What |
|---|---|
| `Connector.cs` | `IConnector`, the registry (`Connectors`), `Secrets` (Windows Credential Manager), `SafeHttp` (host allow-list, https only) |
| `SettingsRow.cs` | `ISettingsConnector`: the connector's row on the settings page; helpers in `SettingsRows` |
| `GitHub/` | GitHub Actions: runs as tasks, its setup window, `github.i18n.json` |
| `Machines/` | the shared base of machine connectors (GPU / cloud machines) and the balance guard, `machines.i18n.json` |
| `Aws/` | AWS, read-only: `Aws.cs` EC2 instances as tasks, `Aws.Jobs.cs` SageMaker and Batch jobs, `Aws.Cost.cs` cost and budgets, `SigV4.cs` the request signer |
| `Discord/` | the Discord bridge (a mirror of notifications like Telegram, not a task connector): `Discord.cs` linking, messages, presses; `Discord.Rest.cs` calls and pacing; `Discord.Gateway.cs` the WebSocket; `discord.i18n.json` |
| `Runpod/`, `VastAi/` | the service adapters on top of `Machines/`, each with its own `<id>.i18n.json` |

## Adding a connector

1. **Folder** `Connectors/<Name>/` with the code and `<id>.i18n.json` (every key in all 10 languages: en uk es de fr it
   pt pl zh ja; it is embedded and loaded with the other translations, `tests/i18n_test.py` checks it).
2. **Class** implementing `ISettingsConnector` (or deriving from `MachineConnector` for machines):
   - `Start` / `Stop` / `State` / `OnControl`, settings in `config.json` under `connectors.<id>`; secrets only through
     `Secrets` (never `config.json`); HTTP only through `SafeHttp` with the service's own hosts.
   - `SettingsRow()`: the row the page draws - on / off, the key, `help_url` (`SettingsRows.KeyHelpUrl`), number inputs
     as `fields` (`SettingsRows.Field(key, panelLabelKey, min, max, step, value)`), `setup` for an own window.
   - `SettingsChange(page)` (usually `SettingsRows.Change(...)`) and `Configure(body)` that validates it.
3. **Register** it in `Program.cs` (`connectors.Add(...)`; the settings page shows connectors in that order) and add its
   credential name to `Secrets.Names` (uninstall `--purge` deletes them).
4. **Key how-to page** `docs/keys/<id>.md` (+ translations), tests with a mock server on a loopback port, `API.md`,
   `PRIVACY.md` (what is sent where) and the README feature list.

The panel needs no code for a new connector unless it brings a widget of its own (like the balance guard).
