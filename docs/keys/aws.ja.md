# AWS: TaskHub 用のアクセスキー

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · **日本語** · [すべてのページ](README.md)

## TaskHub での使い道
- **監視:** 利用中のリージョンの EC2 インスタンスについて、名前、タイプ、リージョン、状態、稼働時間。
  設定した上限（既定は 72 時間）を超えて稼働しているインスタンスには、「Forgotten?」のリマインダーが届きます。
- **長時間ジョブ（オンにした場合）:** SageMaker のトレーニングジョブと AWS Batch のジョブについて、状態、時間、失敗の理由。それぞれ個別の
  スイッチがあり、既定ではオフです。SageMaker は通常のトレーニングジョブの進捗率を返さないため、TaskHub も表示しません。
- **コスト（オンにした場合）:** Cost Explorer による今月のコストと予測（これらのリクエストは AWS で課金されます。下記参照）と、AWS Budgets。予算が
  上限を超えると、月に一度通知が届きます。
- **しないこと:** TaskHub があなたのアカウントで何かを開始、停止、変更することはありません。このポリシーには、そのための権限がありません。

TaskHub は各リージョンを 10 分ごとに読み取ります。AWS の EC2 料金ページにはこれらの呼び出しの料金は載っていませんが、無料であるとは明言されていません。

> このコネクタは AWS のドキュメントをもとに作成し、シミュレーターで検証したもので、実際の AWS アカウントではまだテストしていません。

AWS は一時的な認証情報を推奨しています。TaskHub に必要なのは、この読み取り専用ポリシーを付けた専用 IAM ユーザーの長期キーで、
いつでも無効にできます。ルートユーザーのキーは絶対に使わないでください。

## 必要最小限の権限
この JSON でポリシーを作成します。コネクタの将来のバージョンでは追加の権限が必要になる場合があり、その際はこのページに記載します。

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "TaskHubList",
      "Effect": "Allow",
      "Action": ["ec2:DescribeInstances", "ec2:DescribeRegions"],
      "Resource": "*"
    }
  ]
}
```

長時間ジョブを追跡する場合は、オンにするものに応じて次のステートメントを追加します:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` と `batch:DescribeJobs` はジョブの説明全体を返すため、その中には設定や環境変数が含まれることがあります。
TaskHub が読み取るのは、状態、時間、時間制限、失敗の理由だけです。

コスト（オンにした場合）:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer は有料です。** AWS はリクエスト 1 回につき $0.01 を課金します。TaskHub は 8 時間ごとに最大 2 回リクエストし、月に約 $1.80 です。また、
再起動のたびに支払うことがないよう、結果を保持します。予算の読み取りに料金がかかるとは AWS に記載されていません。Cost Explorer は一度
[Billing コンソール](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html)で有効にする必要があります。API では有効にできず、データが表示されるまで約
1 日かかります。新しいアカウントでは、請求サイクルが 1 周するまで予測が出ないことがあります。AWS Organization では、管理アカウントがメンバーアカウントの
Cost Explorer へのアクセスを、メンバーのポリシーに関係なくブロックできます。

予算を読み取るため、TaskHub はアカウント番号を取得する目的で `sts:GetCallerIdentity` も一度呼び出します。この呼び出しに AWS の権限は不要です。番号はメモリ内にのみ保持されます。

`ec2:DescribeRegions` は、アカウントで有効なリージョンを調べるために使います。これがない場合、TaskHub は自分で指定したリージョンだけを監視し、
何も指定していなければ `us-east-1` を監視します。

## キーを作成する
1. [IAM コンソール](https://console.aws.amazon.com/iam/)で TaskHub 用のユーザーを作成し（コンソールアクセスは不要です）、
   上のポリシーをアタッチします。
2. **Users** を開き、そのユーザーを選び、**Security credentials** タブを開きます。
3. **Access keys** の下で **Create access key** を選びます。
4. ユースケースとして **Other** を選び、**Next**、**Create access key** の順に進みます。
5. **Show** を選び、**Access key ID** と **Secret access key** の両方をコピーします。AWS がシークレットを表示するのは一度だけです。

`ASIA` で始まるキーは一時的なもので、TaskHub は受け付けません。`AKIA` で始まるキーを使ってください。

## TaskHub に貼り付ける
1. TaskHub → **設定** → **コネクタ** → **AWS**。
2. オンにして、アクセスキー ID とシークレットをそれぞれの欄に貼り付け、**保存** をクリックします。
3. 行に **接続済み** と表示されれば成功です。AWS がキーを拒否した場合は、何も保存されません。

**リージョン** を空のままにすると、アカウントで有効なすべてのリージョンを監視します。使うものだけを追加することもできます。

| 行の表示 | 意味 |
|---|---|
| キーは有効ですが、ポリシーで `ec2:DescribeRegions` が許可されていません | 上の権限を追加するか、リージョンを自分で指定してください |
| このリージョンへのアクセス権がありません | ポリシーまたはアカウントがこのリージョンを許可していません。他のリージョンは引き続き動作します |
| SageMaker または AWS Batch: キーにアクセス権がありません | 上のステートメントを追加するか、そのスイッチをオフにしてください |
| Cost Explorer: キーにアクセス権がないか、有効になっていません | 上のステートメントを追加し、Billing コンソールで Cost Explorer を有効にしてください |
| Cost Explorer: まだデータがありません | 有効にしたばかりです。約 1 日お待ちください |
| このコンピューターの時計を確認してください | AWS は時刻が誤ったまま署名されたリクエストを拒否します。時計を直してください。キー自体は問題ありません |

## キーの保管場所
- Windows の資格情報マネージャーのみです。`config.json` には保存されません。2 つの値はまとめて保管され、二度と表示されません。
- シークレット自体は送信されません。リクエストの署名に使われるだけです。リクエストの送信先は `ec2.<region>.amazonaws.com` と、オンにした機能に応じて `api.sagemaker.<region>.amazonaws.com`、`batch.<region>.amazonaws.com`、`ce.us-east-1.amazonaws.com`、`budgets.amazonaws.com`、`sts.amazonaws.com` のみです。

## 無効にする
- **AWS で:** IAM コンソール → **Users** → 該当のユーザー → **Security credentials** → **Access keys** → **Actions** → **Deactivate**、その後
  **Delete**。
- **TaskHub で:** 設定 → コネクタ → AWS → **削除**（資格情報マネージャーからも削除されます）。

## 公式ドキュメント
- アクセスキーの作成、無効化、削除: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- アクセスキーと一時的な認証情報: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- ベストプラクティス: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- 使用する呼び出し: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- 長時間ジョブ: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- コスト: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- リージョン: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
