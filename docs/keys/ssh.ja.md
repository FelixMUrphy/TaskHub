# SSH: TaskHub 用のホストの設定

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · [Deutsch](ssh.de.md) · [Français](ssh.fr.md) · [Italiano](ssh.it.md) · [Português](ssh.pt.md) · [Polski](ssh.pl.md) · [中文](ssh.zh.md) · **日本語** · [すべてのページ](README.md)

## TaskHub が行うこと
- TaskHub は Windows 標準の `ssh.exe` を実行し、登録した各ホストの情報を読み取ります: 負荷、メモリ、ディスク、GPU、tmux セッション、指定したプロセス。
- **TaskHub は鍵もパスワードも保管しません。** あなたの `~/.ssh/config`、SSH エージェント、`known_hosts` を使い、読み取るだけです。
- ホストには何もインストールせず、何も変更しません。現時点では Linux ホストに対応しています。

## ホストを追加する前に
TaskHub は質問に答えられないため、次の 3 つが、何も聞かれずに動く状態になっている必要があります。

1. **OpenSSH クライアント。** TaskHub は Git に付属のものではなく、Windows 標準のものを使います。ターミナルで `C:\Windows\System32\OpenSSH\ssh.exe -V` を実行するとバージョンが表示されるはずです。表示されない場合は、Windows の設定 → システム → オプション機能（Windows 10 ではアプリ → オプション機能）→ *OpenSSH クライアント* を追加してください（[Microsoft の手順](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)）。
2. **入力不要の鍵。** パスフレーズ付きの鍵はエージェントに登録しておく必要があります。管理者として PowerShell を開き、一度だけ次を実行します:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   その後、通常のユーザーとして `ssh-add` を実行します。（[Microsoft の鍵管理ガイド](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement)。）1Password や Pageant のエージェントも、Windows の OpenSSH に対応していれば使えます。
3. **ホストの鍵を信頼済みにする。** ターミナルから一度接続します: `ssh my-server`。表示されたフィンガープリントをホストの管理者から聞いたものと照合し、`yes` と答えてください。TaskHub が新しい鍵を自分で信頼することはありません。

確認方法: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` が、何も聞かれずに `Linux` と表示すれば OK です。

ホストの名前は、ユーザー、ポート、鍵とあわせて `~/.ssh/config` に書きます:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## 追加する
設定 → *コネクタ* → *SSH* → オンにして、`my-server` を入力し *追加* を押します。使えるのは英字、数字、ドット、ハイフン、アンダースコアのみです。`user@host` は config に書いてください。

## 表示される内容
ホストごとにタスクが作られ、負荷とディスクが表示されます。次の場合に通知が届きます: ディスクがいっぱいになったとき、接続が切れたとき、または TaskHub が再試行をやめたとき、ホストが再起動したとき、ジョブが終わったとき。
- **tmux セッション**、またはその中のコマンドがシェルに戻ったとき（エディタを閉じた場合も含みます。設定のスイッチでオフにでき、その場合もタスクのログには残ります）。
- **指定したプロセス:** ホストのタスクで *プロセスを監視* を選び、プロセス番号を指定します（たとえば `pgrep -f train`）。

TaskHub はジョブがどのように終わったかを判断できないため、「成功」ではなく「終了」と表示します。PC の電源が切れている間は、誰も監視していません。その間に終了した指定のプロセスは、次回の接続時に通知されます。tmux の変化とホストの再起動を検知できるのは、TaskHub が動作中に接続が切れたときだけです。TaskHub や PC の再起動をまたいでは検知されません。

## 知っておくこと
- **config が実行されます。** `~/.ssh/config` の `ProxyCommand` や `Match exec` は、ターミナルで接続するときと同じように、TaskHub が接続するたびに実行されます。`ProxyJump` の先のホストには、config にある踏み台ホストの設定が使われます。
- **開いたままになるもの。** コネクタがオンの間、ホストごとに `ssh` が 1 つ接続を維持します。決まった読み取り専用のスクリプトを、既定では 1 分ごと（最短 30 秒）に送ります。
- **鍵が変わると停止します。** ホストの鍵が変わると、TaskHub は接続せず、その旨を知らせます。ホストの再インストールか、途中に第三者がいる可能性があります。確認したうえで、`known_hosts` はご自身で修正してください。
- **ログインの失敗。** 鍵が拒否されると、TaskHub は *再確認* を押すまで再試行をやめます。そのため、あなたのアドレスが締め出されることはありません。
- **PC から送られるもの。** 送り先はあなたのホストだけです: ユーザー名と公開鍵（どの ssh でも同じです）、およびスクリプト。返ってきた内容は TaskHub のデータフォルダーに保存され、通知にも表示されます。Telegram や Discord がオンの場合はそちらにも表示されます。[PRIVACY.md](../../PRIVACY.md) を参照してください。

## 停止する
コネクタをオフにするか、ホストを削除すると、接続は終了します。ホストには何も残らず、TaskHub 側に削除すべき鍵もありません。
