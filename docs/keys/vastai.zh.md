# Vast.ai：为 TaskHub 创建 API 密钥

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · [Deutsch](vastai.de.md) · [Français](vastai.fr.md) · [Italiano](vastai.it.md) · [Português](vastai.pt.md) · [Polski](vastai.pl.md) · **中文** · [日本語](vastai.ja.md) · [所有页面](README.md)

## TaskHub 用它做什么
- **监视：** 你的实例、它们的负载、价格、最近 30 天的费用、你的余额和额度。
- **操作**（仅在密钥允许时）：停止、启动、重启——可在面板、通知或 Telegram 中操作。
- **删除（destroy）**绝不会自动执行。只能在 TaskHub 面板中、输入名称后才能删除。

## 最低权限
| 你的需求 | 权限 |
|---|---|
| (a) 仅监视 | `instance_read`、`user_read`、`billing_read` |
| (b) 停止 / 启动 / 重启 | 上述权限 + `instance_write` |

注意：Vast.ai 的 `instance_write` 涵盖“创建、管理和销毁实例及卷”——没有“只停止 / 启动而不能销毁”的权限。
TaskHub 自己绝不会销毁，只会在面板中、输入名称后才销毁。

## 创建密钥
**在控制台中：** 打开 Vast.ai 控制台的 **Keys** 部分 → **API Keys** → **+New**。在弹出窗口中
输入名称（例如 `TaskHub`）并选择权限——默认会选中你账户的所有权限，所以请只保留表格中的那些。创建后复制密钥。

**用 CLI：** Vast.ai 的权限页面介绍了通过 CLI / API 创建受限密钥的方法——如果控制台
不能缩小权限范围，就用这种方式。保存为 `perms.json`：

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

需要操作时再加上 `"instance_write": {}`，然后运行：

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## 粘贴到 TaskHub
1. TaskHub → **设置** → **连接器** → **Vast.ai**。
2. 打开开关，粘贴密钥，点击 **保存**。
3. 状态行应显示 **已连接，余额 …**（或 **已连接**）。如果 Vast.ai 拒绝该密钥，则不会保存任何内容。

TaskHub 会检查一次（之后每天一次）密钥是否有权执行操作，只有有权时才显示操作按钮。

## 密钥保存在哪里
- 仅保存在 Windows 凭据管理器中——绝不写入 `config.json`。
- 只发送到 `console.vast.ai`。

## 撤销
- **在 Vast.ai：** 控制台 → **Keys** → 密钥旁的 **Delete**（**Reset** 会将其替换为新密钥）。
- **在 TaskHub：** 设置 → 连接器 → Vast.ai → **删除**（同时从凭据管理器中删除）。

## 官方文档
- API 密钥：<https://docs.vast.ai/guides/reference/keys>
- 权限：<https://docs.vast.ai/api-reference/permissions>、<https://docs.vast.ai/cli/permissions>
- 账单和余额：<https://docs.vast.ai/guides/reference/billing>
