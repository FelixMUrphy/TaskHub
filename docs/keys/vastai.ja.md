# Vast.ai: TaskHub 用の API キー

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · [Deutsch](vastai.de.md) · [Français](vastai.fr.md) · [Italiano](vastai.it.md) · [Português](vastai.pt.md) · [Polski](vastai.pl.md) · [中文](vastai.zh.md) · **日本語** · [すべてのページ](README.md)

## TaskHub での使い道
- **監視:** インスタンス、その負荷、料金、過去 30 日間の請求額、残高とクレジット。
- **アクション**（キーが許可している場合のみ）: 停止、起動、リブート。パネル、通知、Telegram から行えます。
- **削除（destroy）**は自動では行いません。TaskHub のパネルで名前を入力した場合にのみ実行できます。

## 必要最小限の権限
| やりたいこと | 権限 |
|---|---|
| (a) 監視のみ | `instance_read`, `user_read`, `billing_read` |
| (b) 停止 / 起動 / リブート | 上記 + `instance_write` |

注意: Vast.ai の `instance_write` は「インスタンスとボリュームの作成、管理、削除」をすべて含みます。削除を除いて
停止 / 起動だけを許可する権限はありません。TaskHub が自分で削除することはなく、パネルで名前を
入力した場合のみです。

## キーを作成する
**コンソールで:** Vast.ai コンソールの **Keys** セクションを開き → **API Keys** → **+New**。表示されるポップアップで
名前（例: `TaskHub`）を付け、権限を選びます。既定ではアカウントのすべての権限が選択されているので、
表にあるものだけを残してください。作成してキーをコピーします。

**CLI で:** Vast.ai の権限に関するページには、CLI / API で権限を絞ったキーを作る方法が書かれています。コンソールで
権限を絞れない場合はこちらを使ってください。次の内容を `perms.json` として保存します:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

操作を行う場合は `"instance_write": {}` を追加し、次を実行します:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## TaskHub に貼り付ける
1. TaskHub → **設定** → **コネクタ** → **Vast.ai**。
2. オンにしてキーを貼り付け、**保存** をクリックします。
3. 行に **接続済み、残高 …**（または **接続済み**）と表示されれば成功です。Vast.ai がキーを拒否した場合は、何も保存されません。

TaskHub は、キーに操作の権限があるかを一度（その後は毎日）確認し、権限がある場合のみ操作ボタンを表示します。

## キーの保管場所
- Windows の資格情報マネージャーのみです。`config.json` には保存されません。
- 送信先は `console.vast.ai` のみです。

## 無効にする
- **Vast.ai で:** コンソール → **Keys** → キーの横の **Delete**（**Reset** は新しいキーに置き換えます）。
- **TaskHub で:** 設定 → コネクタ → Vast.ai → **削除**（資格情報マネージャーからも削除されます）。

## 公式ドキュメント
- API キー: <https://docs.vast.ai/guides/reference/keys>
- 権限: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- 請求と残高: <https://docs.vast.ai/guides/reference/billing>
