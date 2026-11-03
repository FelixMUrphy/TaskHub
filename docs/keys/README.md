# API keys for TaskHub connectors

Some connectors need a key from the provider. Each page says where to create it, the smallest permissions that
work, how to paste it into TaskHub and how to revoke it.

| Connector | English | Українська | Español | Deutsch | Français | Italiano | Português | Polski | 中文 | 日本語 |
|---|---|---|---|---|---|---|---|---|---|---|
| GitHub Actions | [en](github.md) | [uk](github.uk.md) | [es](github.es.md) | [de](github.de.md) | [fr](github.fr.md) | [it](github.it.md) | [pt](github.pt.md) | [pl](github.pl.md) | [zh](github.zh.md) | [ja](github.ja.md) |
| Runpod | [en](runpod.md) | [uk](runpod.uk.md) | [es](runpod.es.md) | [de](runpod.de.md) | [fr](runpod.fr.md) | [it](runpod.it.md) | [pt](runpod.pt.md) | [pl](runpod.pl.md) | [zh](runpod.zh.md) | [ja](runpod.ja.md) |
| Vast.ai | [en](vastai.md) | [uk](vastai.uk.md) | [es](vastai.es.md) | [de](vastai.de.md) | [fr](vastai.fr.md) | [it](vastai.it.md) | [pt](vastai.pt.md) | [pl](vastai.pl.md) | [zh](vastai.zh.md) | [ja](vastai.ja.md) |
| AWS | [en](aws.md) | [uk](aws.uk.md) | [es](aws.es.md) | [de](aws.de.md) | [fr](aws.fr.md) | [it](aws.it.md) | [pt](aws.pt.md) | [pl](aws.pl.md) | [zh](aws.zh.md) | [ja](aws.ja.md) |
| SSH (no key kept) | [en](ssh.md) | [uk](ssh.uk.md) | [es](ssh.es.md) | [de](ssh.de.md) | [fr](ssh.fr.md) | [it](ssh.it.md) | [pt](ssh.pt.md) | [pl](ssh.pl.md) | [zh](ssh.zh.md) | [ja](ssh.ja.md) |
| Discord (a bot token) | [en](discord.md) | [uk](discord.uk.md) | [es](discord.es.md) | [de](discord.de.md) | [fr](discord.fr.md) | [it](discord.it.md) | [pt](discord.pt.md) | [pl](discord.pl.md) | [zh](discord.zh.md) | [ja](discord.ja.md) |

For every connector:
- TaskHub keeps the key only in the Windows Credential Manager — never in `config.json`.
- The key is sent only to the provider's API host (named on each page).
- *Remove* in TaskHub deletes it from the Credential Manager; revoking it at the provider is a separate step.

See also [PRIVACY.md](../../PRIVACY.md).
