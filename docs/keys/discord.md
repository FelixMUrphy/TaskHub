# Discord: bot token for TaskHub

[Українською](discord.uk.md) · [All keys](README.md)

## What TaskHub uses it for
- Sends your TaskHub notifications, task progress and charts as direct messages from **your own** bot.
- Receives your button presses and the `/tasks`, `/mute`, `/unmute` commands, over a connection your PC opens to Discord. No public address is needed.
- It doesn't read your messages: it asks for no privileged intents and never for message content.

## Create the bot
1. Open the Discord Developer Portal: <https://discord.com/developers/applications?new_application=true>.
2. Click **Create App**, enter a name (e.g. `TaskHub`) and click **Create**.
3. On the **Bot** page, under **Token**, click **Reset Token** and copy the token. Discord shows it only once.
4. Keep the bot yours: on the **Installation** page set **Install Link** to **None**, then on the **Bot** page turn off **Public Bot**. With it off, only you can add the bot to servers. (If the Portal refuses, do the **Installation** step first.)
5. Leave the rest as it is: no privileged intents, and **Interactions Endpoint URL** empty. With a URL there, Discord sends button presses to it instead of TaskHub, and TaskHub warns you.

## Paste it into TaskHub
1. TaskHub → **Settings** → **Discord**. Paste the token and click **Connect**. A token Discord refuses isn't saved.
2. A bot can write only to someone it shares a server with. Click **Add the bot to a server** and pick a server of yours. It needs no permissions there.
3. Click **Check again**. The bot sends you a direct message with a **Connect** button. Press it.
4. If no message comes, allow direct messages from that server in its privacy settings and click **Check again**.

Only the application's owner can be linked (for a team's application, the team's owner). Only that person's presses, in that chat, do anything.

## Where the token is kept
- Only in the Windows Credential Manager, never in `config.json`. If the Credential Manager is unavailable, TaskHub says so and doesn't connect.
- Sent only to `discord.com` and Discord's gateway (`*.discord.gg`).
- Your Discord user id, the chat's id and the application's id are stored in `config.json`.

## Revoke it
- **At Discord:** Developer Portal → your application → **Bot** → **Reset Token**. The old token stops working.
- **In TaskHub:** Settings → Discord → **Disconnect** (deletes it from the Credential Manager). The bot stays in your Discord account until you delete the application. Uninstalling TaskHub with **Also delete my data** deletes the token too; a plain uninstall keeps it.

## If the token stops working
Discord resets a bot's token after 1000 new connections in a day and emails the owner. TaskHub stays well below that and waits when it gets close. If it happens anyway, the page says the token was rejected: make a new one and connect again.

## Official docs
- Getting started: <https://docs.discord.com/developers/quick-start/getting-started>
- Bots and direct messages, mutual servers: <https://docs.discord.com/developers/interactions/application-commands>
- Who can add a bot (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL vs the gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Connection limits: <https://docs.discord.com/developers/events/gateway>
- Privacy policy: <https://discord.com/privacy>
