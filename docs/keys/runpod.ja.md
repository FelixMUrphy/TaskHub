# Runpod: TaskHub 用の API キー

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · [Deutsch](runpod.de.md) · [Français](runpod.fr.md) · [Italiano](runpod.it.md) · [Português](runpod.pt.md) · [Polski](runpod.pl.md) · [中文](runpod.zh.md) · **日本語** · [すべてのページ](README.md)

## TaskHub での使い道
- **監視:** Pod、その負荷、料金、これまでの利用額（Runpod の課金情報）、残高と残り稼働見込み。
- **アクション**（キーが許可している場合のみ）: 停止、起動、再起動。パネル、通知、Telegram から行えます。
- **削除**は自動では行いません。TaskHub のパネルで Pod の名前を入力した場合にのみ実行できます。

## キーを作成する
1. Runpod コンソールを開きます: <https://console.runpod.io/user/credentials>。
2. **API Keys** の下で **Create API Key** をクリックします。
3. 名前（例: `TaskHub`）を入力し、タイプを選びます（下記参照）。
4. **Create** をクリックし、すぐにキーをコピーしてください。表示されるのは一度だけで、Runpod には保存されません。

## タイプの選び方
| やりたいこと | タイプ | 備考 |
|---|---|---|
| (a) 監視のみ | **Read Only** | ボタンが不要ならこちらをおすすめします。Read Only キーで課金情報と残高を読み取れるかどうかは Runpod のドキュメントに記載がありません。TaskHub に残高が表示されない場合は、それが理由です。 |
| (b) 停止 / 起動 / 再起動 | **All** | Runpod が **Restricted** スコープを文書化しているのは Serverless エンドポイントだけで、Pod についてはありません。そのため Pod の操作には **All** を使います。 |

TaskHub は、キーに操作の権限があるかを一度（その後は毎日）確認し、権限がある場合のみ操作ボタンを表示します。
タイプ **All** のキーは Pod の削除もできますが、TaskHub が自分で削除することはありません。パネルで
名前を入力した場合のみです。

## TaskHub に貼り付ける
1. TaskHub → **設定** → **コネクタ** → **Runpod**。
2. オンにしてキーを貼り付け、**保存** をクリックします。
3. 行に **接続済み、残高 …**（または **接続済み**）と表示されれば成功です。Runpod がキーを拒否した場合は、何も保存されません。

## キーの保管場所
- Windows の資格情報マネージャーのみです。`config.json` には保存されません。
- 送信先は `api.runpod.io` のみです（Pod、ネットワークボリュームのサイズ、課金は REST、残高は GraphQL）。

## 無効にする
- **Runpod で:** <https://console.runpod.io/user/credentials> → キーの横のごみ箱アイコン → **Revoke Key**
  （またはキーのトグルでオフにします）。
- **TaskHub で:** 設定 → コネクタ → Runpod → **削除**（資格情報マネージャーからも削除されます）。
  TaskHub をアンインストールするときに **データも削除する** を選ぶと、こちらも削除されます。通常のアンインストールでは残ります。

## 公式ドキュメント
- API キー: <https://docs.runpod.io/get-started/credentials>
- レート制限: <https://docs.runpod.io/api-reference-v2/rate-limits>
- 料金と、残高が $0 になったときの動作: <https://docs.runpod.io/pods/pricing>
