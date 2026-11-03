# GitHub Actions: TaskHub 用のトークン

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · [Deutsch](github.de.md) · [Français](github.fr.md) · [Italiano](github.it.md) · [Português](github.pt.md) · [Polski](github.pl.md) · [中文](github.zh.md) · **日本語** · [すべてのページ](README.md)

## TaskHub での使い道
- **監視:** 指定したリポジトリの workflow 実行について、進行状況、失敗したステップ、結果を表示します。
- **アクション**（書き込み権限がある場合のみ）: 実行のキャンセルと再実行。
- **プルリクエスト**（任意、2.9.4 以降、読み取りのみ）: あなたのレビュー待ちのものと、オープン中の Dependabot のプルリクエスト。
- TaskHub が workflow、コード、リポジトリの設定を変更することはありません。

## fine-grained トークンを作成する
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token**（直接リンク: <https://github.com/settings/personal-access-tokens/new>）。
2. **Name:** 例: `TaskHub`。
3. **Expiration:** 既定は 30 日です。都合に合わせて選んでください（期限の 1 週間前に TaskHub が警告します）。
4. **Resource owner:** ご自身、またはリポジトリを所有する組織。
5. **Repository access:** **Only select repositories** → TaskHub に登録するリポジトリを選びます。
6. **Permissions**（下記参照）→ **Generate token** → トークンをコピーします。

所有者が承認を必要とする組織の場合、管理者が承認するまでトークンは保留のままです。

## 必要最小限の権限
| やりたいこと | Repository permissions |
|---|---|
| (a) 監視のみ | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) キャンセル / 再実行 | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

プルリクエスト（任意）: Dependabot のプルリクエストには **Pull requests: Read-only** も必要です（GitHub はオープン中の PR の
一覧をこの権限でのみ返します）。レビュー依頼の検索には権限は不要です。fine-grained トークンでレビュー依頼が見えるのは、
トークンに許可したリポジトリと公開リポジトリだけです。

このトークンは、TaskHub のもう一つの選択肢（Git Credential Manager が既に持っているトークン）より、こちらをおすすめします。
そちらはすべてのリポジトリにアクセスできてしまいます。

## あとから監視するリポジトリを増やす
**Only select repositories** を選んだ場合、トークンが届くのは選んだリポジトリだけです。TaskHub に
*見つからないか、トークンにアクセス権がありません* と表示されたら、まず名前を確認し、次に GitHub でトークンを開いて（**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**）、**Repository access** にそのリポジトリを追加してください。GitHub は、
名前の間違い、トークンに許可されていないリポジトリ、リクエストによっては権限の不足のいずれでも同じ応答を返すため、TaskHub はどれが原因かを区別できません。
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## TaskHub に貼り付ける
1. TaskHub → **設定** → **コネクタ** → **GitHub Actions**。
2. オンにして **自分の fine-grained トークン（推奨）** を選び、トークンを貼り付け、リポジトリ（`owner/repo`）を
   入力して **保存** をクリックします。
3. 行に **{ログイン名} として接続済み** と表示されれば成功です。GitHub がトークンを拒否した場合は、何も保存されません。

## トークンの保管場所
- Windows の資格情報マネージャーのみです。`config.json` には保存されません。
- 送信先は `api.github.com` のみです。任意のログ末尾は、GitHub が発行する一回限りのリンクから、
  トークンなしで取得します。

## 無効にする
- **GitHub で:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → 該当のトークン →
  **Delete**。
- **TaskHub で:** 設定 → コネクタ → GitHub Actions → **削除**（資格情報マネージャーからも削除されます）。

## 公式ドキュメント
- fine-grained トークンの作成:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- プルリクエストの検索（`review-requested:`）:
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- エンドポイントごとの権限:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
