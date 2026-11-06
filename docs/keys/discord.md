# Discord: bot token for TaskHub

**English** · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · [Français](discord.fr.md) · [Italiano](discord.it.md) · [Português](discord.pt.md) · [Polski](discord.pl.md) · [中文](discord.zh.md) · [日本語](discord.ja.md) · [All pages](README.md)

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
1. TaskHub → **Settings** → **Messengers** → **Discord**. Paste the token and click **Connect**. A token Discord refuses isn't saved.
2. Add the bot to a server (next section). Discord lets a bot write only to someone it shares a server with, so until then TaskHub says the bot can't write to you yet.
3. Click **Check again**. The bot sends you a direct message with a **Connect** button. Press it.

Only the application's owner can be linked (for a team's application, the team's owner). Only that person's presses, in that chat, do anything.

## Add the bot to a server
1. In TaskHub, click **Add the bot to a server**. Discord's page opens in your browser.
2. In the list of servers, pick one where you may add bots: you own it or have **Manage Server** there. The bot gets no permissions in it and doesn't read it; it only needs to share a server with you.
3. Confirm on that page, then go back to TaskHub and click **Check again**.

**No server of your own?** Creating one is free: in the Discord app, click **Add a Server** at the bottom of the left sidebar, choose **Create My Own**, then **For me and my friends**, enter a name and click **Create**. Then repeat the steps above.

**Still no message?** Your privacy settings may block it: click the server's name, open **Privacy Settings**, turn on **Allow direct messages from server members**, and click **Check again**.

## Where the token is kept
- Only in the Windows Credential Manager, never in `config.json`. If the Credential Manager is unavailable, TaskHub says so and doesn't connect.
- Sent only to `discord.com` and Discord's gateway (`*.discord.gg`).
- Your Discord user id, the chat's id and the application's id are stored in `config.json`.

## Revoke it
- **At Discord:** Developer Portal → your application → **Bot** → **Reset Token**. The old token stops working.
- **In TaskHub:** Settings → Messengers → Discord → **Disconnect** (deletes it from the Credential Manager). The bot stays in your Discord account until you delete the application. Uninstalling TaskHub with **Also delete my data** deletes the token too; a plain uninstall keeps it.

## If the token stops working
Discord resets a bot's token after 1000 new connections in a day and emails the owner. TaskHub stays well below that and waits when it gets close. If it happens anyway, the page says the token was rejected: make a new one and connect again.

## Official docs
- Getting started: <https://docs.discord.com/developers/quick-start/getting-started>
- Bots and direct messages, mutual servers: <https://docs.discord.com/developers/interactions/application-commands>
- Creating a server: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Direct messages from server members: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Who can add a bot (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL vs the gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Connection limits: <https://docs.discord.com/developers/events/gateway>
- Privacy policy: <https://discord.com/privacy>
