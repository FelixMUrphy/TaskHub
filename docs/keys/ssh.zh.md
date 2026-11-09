# SSH：为 TaskHub 设置主机

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · [Deutsch](ssh.de.md) · [Français](ssh.fr.md) · [Italiano](ssh.it.md) · [Português](ssh.pt.md) · [Polski](ssh.pl.md) · **中文** · [日本語](ssh.ja.md) · [所有页面](README.md)

## TaskHub 用它做什么
- TaskHub 运行 Windows 自带的 `ssh.exe`，读取你列出的每台主机：负载、内存、磁盘、GPU、tmux 会话，以及你指定的某个进程。
- **TaskHub 不保存任何密钥或密码。** 它使用你的 `~/.ssh/config`、SSH agent 和 `known_hosts`，而且只读取它们。
- 它不在主机上安装任何东西，也不更改主机上的任何内容。目前仅支持 Linux 主机。

## 添加主机之前
TaskHub 无法回答提问，所以下面三件事必须能在无需任何提问的情况下正常工作。

1. **OpenSSH 客户端。** TaskHub 使用 Windows 自带的，而不是 Git 附带的副本。在终端中，`C:\Windows\System32\OpenSSH\ssh.exe -V` 应输出版本号。如果没有：Windows 设置 → 系统 → 可选功能（Windows 10：应用 → 可选功能）→ 添加 *OpenSSH 客户端*（[Microsoft 的步骤](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)）。
2. **无需输入的密钥。** 带密码短语的密钥必须已加入 agent。以管理员身份在 PowerShell 中执行一次：
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   然后以你自己的身份执行：`ssh-add`。（[Microsoft 的密钥指南](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement)。）1Password 或 Pageant 的 agent 只要能为 Windows 的 OpenSSH 提供服务，也可以使用。
3. **主机的密钥已被信任。** 在终端中连接一次：`ssh my-server`。把显示的指纹与主机所有者提供的指纹对比，然后回答 `yes`。TaskHub 绝不会自行信任新密钥。

检查方法：`C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` 无需任何提问就输出 `Linux`。

主机的名称、用户、端口和密钥都写在 `~/.ssh/config` 中：
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## 添加
设置 → *连接器* → *SSH* → 打开开关，输入 `my-server`，然后按 *添加*。只能使用字母、数字、点、短横线和下划线：`user@host` 要写在配置文件中。

## 你会看到什么
每台主机对应一个任务，显示它的负载和磁盘。磁盘写满、连接丢失或 TaskHub 不得不停止重试、主机重启以及某个作业结束时，你都会收到通知：
- **一个 tmux 会话**，或其中的某条命令回到了 shell（关闭编辑器也算；设置中有开关可以关闭这类通知，它们仍会保留在任务日志中）；
- **你指定的进程：** 在主机的任务上点 *监视进程*，并填入进程号（例如 `pgrep -f train`）。

TaskHub 无法判断作业是如何结束的，所以只会说“已结束”，而不是“成功”。你的电脑关机期间没有人在监视。你指定的进程如果在此期间结束，会在下次连接时报告。tmux 的变化和主机的重启，只有在 TaskHub 持续运行、连接中断的情况下才会被发现：TaskHub 或你的电脑重启后则不会。

## 需要了解的事项
- **你的配置会被执行。** `~/.ssh/config` 中的 `ProxyCommand` 或 `Match exec` 会在 TaskHub 的每次连接时运行，就像你在终端中连接时一样。位于 `ProxyJump` 之后的主机会使用你配置中为跳板机设定的参数。
- **会保持打开什么。** 连接器开启期间，每台主机一个 `ssh`。它默认每分钟发送一次固定的只读脚本（最短 30 秒）。
- **密钥变化会让它停下。** 如果主机的密钥变了，TaskHub 不会连接并会告诉你。可能是主机重装了，也可能有人在中间截获：先核实，再自行修复 `known_hosts`。
- **登录失败。** 密钥被拒绝后，TaskHub 会停止尝试，直到你按 *重新检查*，所以不会导致你的地址被封。
- **哪些内容会离开你的电脑。** 只发往你的主机：你的用户名和公钥（和任何 ssh 一样），以及脚本。返回的内容保存在 TaskHub 的数据文件夹中，并显示在通知里，开启时也包括 Telegram 或 Discord。参见 [PRIVACY.md](../../PRIVACY.md)。

## 停止
关闭连接器或移除主机：连接随即结束。主机上不会留下任何东西，TaskHub 中也没有需要删除的密钥。
