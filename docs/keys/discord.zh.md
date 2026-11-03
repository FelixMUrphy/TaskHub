# Discord：为 TaskHub 创建机器人令牌

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · [Français](discord.fr.md) · [Italiano](discord.it.md) · [Português](discord.pt.md) · [Polski](discord.pl.md) · **中文** · [日本語](discord.ja.md) · [所有页面](README.md)

## TaskHub 用它做什么
- 通过**你自己的**机器人，以私信形式发送 TaskHub 的通知、任务进度和图表。
- 通过你的电脑向 Discord 建立的连接，接收你的按钮点击以及 `/tasks`、`/mute`、`/unmute` 命令。无需公网地址。
- 它不会读取你的消息：不申请任何特权 intent，也从不读取消息内容。

## 创建机器人
1. 打开 Discord Developer Portal：<https://discord.com/developers/applications?new_application=true>。
2. 点击 **Create App**，输入名称（例如 `TaskHub`），然后点击 **Create**。
3. 在 **Bot** 页面的 **Token** 下点击 **Reset Token** 并复制令牌。Discord 只显示一次。
4. 让机器人只属于你：在 **Installation** 页面把 **Install Link** 设为 **None**，然后在 **Bot** 页面关闭 **Public Bot**。关闭后，只有你能把机器人添加到服务器。（如果 Portal 拒绝，请先完成 **Installation** 这一步。）
5. 其余保持默认：不启用特权 intent，**Interactions Endpoint URL** 保持为空。如果填写了 URL，Discord 会把按钮点击发送到该地址而不是 TaskHub，TaskHub 会向你发出警告。

## 粘贴到 TaskHub
1. TaskHub → **设置** → **Discord**。粘贴令牌并点击 **连接**。Discord 拒绝的令牌不会被保存。
2. 把机器人添加到一个服务器（见下一节）。Discord 只允许机器人给与它共处同一服务器的人发消息，所以在此之前，TaskHub 会提示机器人还不能给你发消息。
3. 点击 **再次检查**。机器人会给你发一条带有 **连接** 按钮的私信。点击它。

只有应用的所有者可以被关联（团队的应用则是团队所有者）。只有此人在该聊天中的点击才会生效。

## 把机器人添加到服务器
1. 在 TaskHub 中点击 **将机器人添加到服务器**。Discord 的页面会在浏览器中打开。
2. 在服务器列表中选择一个你可以添加机器人的服务器：你是它的所有者，或在其中拥有 **Manage Server** 权限。机器人在其中不会获得任何权限，也不会读取其内容；它只需要和你共处同一个服务器。
3. 在该页面上确认，然后回到 TaskHub，点击 **再次检查**。

**没有自己的服务器？** 创建服务器是免费的：在 Discord 应用中，点击左侧栏底部的 **Add a Server**，选择 **Create My Own**，再选择 **For me and my friends**，输入名称并点击 **Create**。然后重复上面的步骤。

**仍然没有收到消息？** 可能是你的隐私设置阻止了它：点击服务器名称，打开 **Privacy Settings**，打开 **Allow direct messages from server members**，然后点击 **再次检查**。

## 令牌保存在哪里
- 仅保存在 Windows 凭据管理器中，绝不写入 `config.json`。如果凭据管理器不可用，TaskHub 会说明情况并且不会连接。
- 只发送到 `discord.com` 和 Discord 的网关（`*.discord.gg`）。
- 你的 Discord 用户 ID、聊天 ID 和应用 ID 保存在 `config.json` 中。

## 撤销
- **在 Discord：** Developer Portal → 你的应用 → **Bot** → **Reset Token**。旧令牌随即失效。
- **在 TaskHub：** 设置 → Discord → **断开**（同时从凭据管理器中删除）。在你删除应用之前，机器人仍保留在你的 Discord 账户中。卸载 TaskHub 时选择 **同时删除我的数据** 也会删除令牌；普通卸载则会保留。

## 如果令牌失效
Discord 在一天内出现 1000 次新连接后会重置机器人的令牌，并给所有者发邮件。TaskHub 远低于这个数量，接近时还会等待。如果仍然发生，页面会提示令牌被拒绝：请生成新令牌并重新连接。

## 官方文档
- 入门：<https://docs.discord.com/developers/quick-start/getting-started>
- 机器人与私信、共同服务器：<https://docs.discord.com/developers/interactions/application-commands>
- 创建服务器：<https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- 来自服务器成员的私信：<https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- 谁可以添加机器人（Public Bot）：<https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL 与网关：<https://docs.discord.com/developers/interactions/receiving-and-responding>
- 连接限制：<https://docs.discord.com/developers/events/gateway>
- 隐私政策：<https://discord.com/privacy>
