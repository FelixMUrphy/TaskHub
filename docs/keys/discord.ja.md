# Discord: TaskHub 用のボットトークン

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · [Français](discord.fr.md) · [Italiano](discord.it.md) · [Português](discord.pt.md) · [Polski](discord.pl.md) · [中文](discord.zh.md) · **日本語** · [すべてのページ](README.md)

## TaskHub での使い道
- TaskHub の通知、タスクの進行状況、グラフを、**あなた自身の**ボットからダイレクトメッセージとして送ります。
- ボタンの操作と、`/tasks`、`/mute`、`/unmute` コマンドを、PC から Discord へ開いた接続で受け取ります。公開アドレスは不要です。
- あなたのメッセージは読みません。特権インテントは要求せず、メッセージの内容も要求しません。

## ボットを作成する
1. Discord Developer Portal を開きます: <https://discord.com/developers/applications?new_application=true>。
2. **Create App** をクリックし、名前（例: `TaskHub`）を入力して **Create** をクリックします。
3. **Bot** ページの **Token** の下で **Reset Token** をクリックし、トークンをコピーします。Discord が表示するのは一度だけです。
4. ボットを自分専用にします: **Installation** ページで **Install Link** を **None** にし、次に **Bot** ページで **Public Bot** をオフにします。オフにすると、ボットをサーバーに追加できるのはあなただけになります。（Portal に拒否された場合は、先に **Installation** の手順を行ってください。）
5. 残りは変更しません: 特権インテントはなし、**Interactions Endpoint URL** は空のままにします。ここに URL があると、Discord はボタンの操作を TaskHub ではなくその URL に送り、TaskHub が警告を表示します。

## TaskHub に貼り付ける
1. TaskHub → **設定** → **メッセンジャー** → **Discord**。トークンを貼り付けて **接続** をクリックします。Discord が拒否したトークンは保存されません。
2. ボットをサーバーに追加します（次のセクション）。Discord では、ボットが書き込めるのは共通のサーバーにいる相手だけなので、追加するまでは、ボットがまだあなたに書き込めないと TaskHub に表示されます。
3. **再確認** をクリックします。ボットが **接続** ボタン付きのダイレクトメッセージを送ってきます。それを押してください。

連携できるのはアプリケーションの所有者だけです（チームのアプリケーションの場合はチームの所有者）。そのチャットでその人が押した場合のみ、操作が有効になります。

## ボットをサーバーに追加する
1. TaskHub で **ボットをサーバーに追加** をクリックします。ブラウザーで Discord のページが開きます。
2. サーバーの一覧から、ボットを追加できるものを選びます。自分がオーナーのサーバーか、**Manage Server** 権限があるサーバーです。ボットはそのサーバーでの権限を一切持たず、内容も読みません。あなたと共通のサーバーにいることだけが必要です。
3. そのページで確定し、TaskHub に戻って **再確認** をクリックします。

**自分のサーバーがない場合は?** サーバーは無料で作成できます。Discord アプリの左サイドバー下部にある **Add a Server** をクリックし、**Create My Own**、次に **For me and my friends** を選び、名前を入力して **Create** をクリックします。そのあと、上の手順をやり直してください。

**まだメッセージが届かない場合は?** プライバシー設定でブロックされている可能性があります。サーバー名をクリックして **Privacy Settings** を開き、**Allow direct messages from server members** をオンにして、**再確認** をクリックしてください。

## トークンの保管場所
- Windows の資格情報マネージャーのみです。`config.json` には保存されません。資格情報マネージャーが使えない場合、TaskHub はその旨を表示し、接続しません。
- 送信先は `discord.com` と Discord のゲートウェイ（`*.discord.gg`）のみです。
- あなたの Discord ユーザー ID、チャットの ID、アプリケーションの ID は `config.json` に保存されます。

## 無効にする
- **Discord で:** Developer Portal → 該当のアプリケーション → **Bot** → **Reset Token**。古いトークンは使えなくなります。
- **TaskHub で:** 設定 → メッセンジャー → Discord → **切断**（資格情報マネージャーからも削除されます）。ボットは、アプリケーションを削除するまで Discord アカウントに残ります。TaskHub をアンインストールするときに **データも削除する** を選ぶと、トークンも削除されます。通常のアンインストールでは残ります。

## トークンが使えなくなったら
Discord は、1 日に 1000 回新しく接続されるとボットのトークンをリセットし、所有者にメールで知らせます。TaskHub の接続回数はそれを大きく下回り、上限に近づくと待機します。それでも起きた場合は、ページにトークンが拒否されたと表示されます。新しいトークンを作成して、もう一度接続してください。

## 公式ドキュメント
- Getting started: <https://docs.discord.com/developers/quick-start/getting-started>
- ボットとダイレクトメッセージ、共通のサーバー: <https://docs.discord.com/developers/interactions/application-commands>
- サーバーの作成: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- サーバーメンバーからのダイレクトメッセージ: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- ボットを追加できる人（Public Bot）: <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL とゲートウェイ: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- 接続の上限: <https://docs.discord.com/developers/events/gateway>
- プライバシーポリシー: <https://discord.com/privacy>
