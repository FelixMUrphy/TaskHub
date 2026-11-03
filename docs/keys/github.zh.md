# GitHub Actions：为 TaskHub 创建令牌

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · [Deutsch](github.de.md) · [Français](github.fr.md) · [Italiano](github.it.md) · [Português](github.pt.md) · [Polski](github.pl.md) · **中文** · [日本語](github.ja.md) · [所有页面](README.md)

## TaskHub 用它做什么
- **监视：** 你列出的仓库中的 workflow 运行——进度、失败的步骤、结果。
- **操作**（仅在有写入权限时）：取消和重新运行某次运行。
- **Pull request**（可选，自 2.9.4 起，只读）：等待你审查的 pull request，以及未关闭的 Dependabot pull request。
- TaskHub 绝不会修改你的 workflow、代码或仓库设置。

## 创建 fine-grained 令牌
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token**（直达链接：<https://github.com/settings/personal-access-tokens/new>）。
2. **Name：** 例如 `TaskHub`。
3. **Expiration：** 默认 30 天；可按需选择（令牌到期前一周 TaskHub 会提醒你）。
4. **Resource owner：** 你本人，或拥有这些仓库的组织。
5. **Repository access：** **Only select repositories** → 选择你将在 TaskHub 中列出的仓库。
6. **Permissions**（见下文）→ **Generate token** → 复制令牌。

如果所有者是需要审批的组织，令牌会保持待处理状态，直到管理员批准。

## 最低权限
| 你的需求 | 仓库权限 |
|---|---|
| (a) 仅监视 | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) 取消 / 重新运行 | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull request（可选）：Dependabot pull request 还需要 **Pull requests: Read-only**（GitHub 只在该权限下提供未关闭 PR 的列表）；
搜索待审查请求则不需要任何权限。fine-grained 令牌只能看到它被授权的仓库和公开仓库中的审查请求。

建议优先使用这种令牌，而不是 TaskHub 的另一个选项（Git Credential Manager 已有的令牌），后者可以访问你的所有仓库。

## 以后再监视一个仓库
选择 **Only select repositories** 后，令牌只能访问你选定的仓库。如果 TaskHub 提示某个仓库
*not found, or the token can't reach it*（未找到，或令牌无法访问）：先检查名称，然后在 GitHub 上打开该令牌（**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**），在 **Repository access** 下添加该仓库。对于名称错误、令牌未被授权的仓库，以及某些请求中缺少权限的情况，GitHub 的回应都一样，
所以 TaskHub 无法告诉你究竟是哪一种。
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## 粘贴到 TaskHub
1. TaskHub → **设置** → **连接器** → **GitHub Actions**。
2. 打开开关，选择 **我自己的 fine-grained 令牌（推荐）**，粘贴令牌，列出仓库
   （`owner/repo`），点击 **保存**。
3. 状态行应显示 **已连接为** *你的登录名*。如果 GitHub 拒绝该令牌，则不会保存任何内容。

## 令牌保存在哪里
- 仅保存在 Windows 凭据管理器中——绝不写入 `config.json`。
- 只发送到 `api.github.com`。可选的日志末尾内容通过 GitHub 给出的一次性链接获取，
  不带令牌。

## 撤销
- **在 GitHub：** Settings → Developer settings → Personal access tokens → Fine-grained tokens → 该令牌 →
  **Delete**。
- **在 TaskHub：** 设置 → 连接器 → GitHub Actions → **移除**（同时从凭据管理器中删除）。

## 官方文档
- 创建 fine-grained 令牌：
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- 搜索 pull request（`review-requested:`）：
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- 各端点所需权限：
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
