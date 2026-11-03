# API keys for TaskHub connectors

Some connectors need a key from the provider. Each page says where to create it, the smallest permissions that
work, how to paste it into TaskHub and how to revoke it.

| Connector | English | Українська |
|---|---|---|
| GitHub Actions | [github.md](github.md) | [github.uk.md](github.uk.md) |
| Runpod | [runpod.md](runpod.md) | [runpod.uk.md](runpod.uk.md) |
| Vast.ai | [vastai.md](vastai.md) | [vastai.uk.md](vastai.uk.md) |
| AWS | [aws.md](aws.md) | [aws.uk.md](aws.uk.md) |
| SSH (no key kept) | [ssh.md](ssh.md) | [ssh.uk.md](ssh.uk.md) |
| Discord (a bot token) | [discord.md](discord.md) | [discord.uk.md](discord.uk.md) |

For every connector:
- TaskHub keeps the key only in the Windows Credential Manager — never in `config.json`.
- The key is sent only to the provider's API host (named on each page).
- *Remove* in TaskHub deletes it from the Credential Manager; revoking it at the provider is a separate step.

See also [PRIVACY.md](../../PRIVACY.md).
