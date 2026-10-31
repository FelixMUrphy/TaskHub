# Vast.ai: API key for TaskHub

[Українською](vastai.uk.md) · [All keys](README.md)

## What TaskHub uses it for
- **Watch:** your instances, their load, price, charges of the last 30 days, your balance and credit.
- **Actions** (only if the key allows them): Stop, Start, Reboot — from the panel, a notification or Telegram.
- **Delete (destroy)** is never done automatically. It works only in the TaskHub panel, after you type the name.

## Minimum permissions
| You want | Permissions |
|---|---|
| (a) Watch only | `instance_read`, `user_read`, `billing_read` |
| (b) Stop / Start / Reboot | the above + `instance_write` |

Caveat: Vast.ai's `instance_write` covers "creating, managing, and destroying instances and volumes" — there is
no permission for stop / start without destroy. TaskHub never destroys by itself, only from the panel after you
type the name.

## Create the key
**In the console:** open the **Keys** section of the Vast.ai console → **API Keys** → **+New**. In the pop-up
give it a name (e.g. `TaskHub`) and choose the permissions — all your account permissions are selected by
default, so leave only those from the table. Create it and copy the key.

**With the CLI:** Vast.ai's permissions pages describe constrained keys through the CLI / API — use this if the
console doesn't let you narrow them. Save as `perms.json`:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Add `"instance_write": {}` for actions, then run:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## Paste it into TaskHub
1. TaskHub → **Settings** → **Connectors** → **Vast.ai**.
2. Turn it on, paste the key, click **Save**.
3. The line should read **Connected, balance …** (or **Connected**). If Vast.ai rejects the key, nothing is saved.

TaskHub checks once (and then daily) whether the key may act and shows the action buttons only if it may.

## Where the key is kept
- Only in the Windows Credential Manager — never in `config.json`.
- Sent only to `console.vast.ai`.

## Revoke it
- **At Vast.ai:** console → **Keys** → **Delete** next to the key (**Reset** replaces it with a new one).
- **In TaskHub:** Settings → Connectors → Vast.ai → **Remove** (deletes it from the Credential Manager).

## Official docs
- API keys: <https://docs.vast.ai/guides/reference/keys>
- Permissions: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Billing and balance: <https://docs.vast.ai/guides/reference/billing>
