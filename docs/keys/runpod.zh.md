# Runpod：为 TaskHub 创建 API 密钥

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · [Deutsch](runpod.de.md) · [Français](runpod.fr.md) · [Italiano](runpod.it.md) · [Português](runpod.pt.md) · [Polski](runpod.pl.md) · **中文** · [日本語](runpod.ja.md) · [所有页面](README.md)

## TaskHub 用它做什么
- **监视：** 你的 pod、它们的负载、价格、已花费金额（Runpod 账单）、你的余额和可用时长。
- **操作**（仅在密钥允许时）：停止、启动、重启——可在面板、通知或 Telegram 中操作。
- **删除**绝不会自动执行。只能在 TaskHub 面板中、输入 pod 名称后才能删除。

## 创建密钥
1. 打开 Runpod 控制台：<https://console.runpod.io/user/credentials>。
2. 在 **API Keys** 下点击 **Create API Key**。
3. 输入名称（例如 `TaskHub`），并选择类型（见下文）。
4. 点击 **Create**，并立即复制密钥——它只显示一次；Runpod 不会保存它。

## 选择哪种类型
| 你的需求 | 类型 | 说明 |
|---|---|---|
| (a) 仅监视 | **Read Only** | 如果不需要按钮，推荐使用。Runpod 没有说明 Read Only 密钥能否读取账单和余额；如果 TaskHub 不显示余额，原因就在这里。 |
| (b) 停止 / 启动 / 重启 | **All** | Runpod 只为 Serverless 端点提供 **Restricted** 范围，pod 没有——所以要对 pod 执行操作，请使用 **All**。 |

TaskHub 会检查一次（之后每天一次）密钥是否有权执行操作，只有有权时才显示操作按钮。
**All** 类型的密钥也可以删除 pod——TaskHub 自己绝不会这样做，只会在面板中、输入名称后才删除。

## 粘贴到 TaskHub
1. TaskHub → **设置** → **连接器** → **Runpod**。
2. 打开开关，粘贴密钥，点击 **保存**。
3. 状态行应显示 **已连接，余额 …**（或 **已连接**）。如果 Runpod 拒绝该密钥，则不会保存任何内容。

## 密钥保存在哪里
- 仅保存在 Windows 凭据管理器中——绝不写入 `config.json`。
- 只发送到 `api.runpod.io`（pod、网络卷大小和账单用 REST，余额用 GraphQL）。

## 撤销
- **在 Runpod：** <https://console.runpod.io/user/credentials> → 密钥旁的垃圾桶图标 → **Revoke Key**
  （或用开关关闭该密钥）。
- **在 TaskHub：** 设置 → 连接器 → Runpod → **删除**（同时从凭据管理器中删除）。
  卸载 TaskHub 时选择 **同时删除我的数据** 也会删除它；普通卸载则会保留。

## 官方文档
- API 密钥：<https://docs.runpod.io/get-started/credentials>
- 速率限制：<https://docs.runpod.io/api-reference-v2/rate-limits>
- 定价，以及余额为 $0 时会发生什么：<https://docs.runpod.io/pods/pricing>
