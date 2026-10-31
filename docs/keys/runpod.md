# Runpod: API key for TaskHub

[Українською](runpod.uk.md) · [All keys](README.md)

## What TaskHub uses it for
- **Watch:** your pods, their load, price, spent so far (Runpod billing), your balance and runway.
- **Actions** (only if the key allows them): Stop, Start, Restart — from the panel, a notification or Telegram.
- **Delete** is never done automatically. It works only in the TaskHub panel, after you type the pod's name.

## Create the key
1. Open the Runpod console: <https://console.runpod.io/user/credentials>.
2. Under **API Keys** click **Create API Key**.
3. Enter a name (e.g. `TaskHub`) and choose the type (see below).
4. Click **Create** and copy the key right away — it is shown only once; Runpod doesn't store it.

## Which type to choose
| You want | Type | Notes |
|---|---|---|
| (a) Watch only | **Read Only** | Recommended if you don't need the buttons. Runpod doesn't document whether a Read Only key may read billing and the balance; if TaskHub shows no balance, that is the reason. |
| (b) Stop / Start / Restart | **All** | Runpod documents **Restricted** scopes only for Serverless endpoints, not for pods — so for pod actions use **All**. |

TaskHub checks once (and then daily) whether the key may act and shows the action buttons only if it may.
A key of type **All** could also delete pods — TaskHub never does that by itself, only from the panel after
you type the name.

## Paste it into TaskHub
1. TaskHub → **Settings** → **Connectors** → **Runpod**.
2. Turn it on, paste the key, click **Save**.
3. The line should read **Connected, balance …** (or **Connected**). If Runpod rejects the key, nothing is saved.

## Where the key is kept
- Only in the Windows Credential Manager — never in `config.json`.
- Sent only to `api.runpod.io` (REST for pods and billing, GraphQL for the balance).

## Revoke it
- **At Runpod:** <https://console.runpod.io/user/credentials> → the trash icon next to the key → **Revoke Key**
  (or switch the key off with its toggle).
- **In TaskHub:** Settings → Connectors → Runpod → **Remove** (deletes it from the Credential Manager).
  Uninstalling TaskHub deletes it too.

## Official docs
- API keys: <https://docs.runpod.io/get-started/credentials>
- Rate limits: <https://docs.runpod.io/api-reference-v2/rate-limits>
- Pricing, and what happens at $0: <https://docs.runpod.io/pods/pricing>
