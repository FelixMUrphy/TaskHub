# AWS：为 TaskHub 创建访问密钥

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · **中文** · [日本語](aws.ja.md) · [所有页面](README.md)

## TaskHub 用它做什么
- **监视：** 你所用区域中的 EC2 实例：名称、类型、区域、状态以及已运行的时长。
  运行时间超过你设定上限（默认 72 小时）的实例会收到“是否忘记了？”的提醒。
- **长时间作业（需手动开启）：** SageMaker 训练作业和 AWS Batch 作业：状态、时间、失败原因。各有独立的
  开关，默认关闭。SageMaker 不会为普通训练作业报告百分比，所以 TaskHub 也不显示。
- **费用（需手动开启）：** Cost Explorer 提供的本月费用和预测（这些请求 AWS 会收费，见下文）以及你的 AWS Budgets。预算
  超出限额时，每月会收到一次通知。
- **绝不会：** TaskHub 不会启动、停止或更改你账户中的任何内容。这份策略也不给它这样做的权限。

TaskHub 每 10 分钟读取一次每个区域。AWS 的 EC2 定价页面没有列出这些调用的费用，但 AWS 也没有明确说它们免费。

> 此连接器根据 AWS 文档构建并通过模拟器测试，尚未在真实 AWS 账户上验证。

AWS 更推荐使用临时凭证。TaskHub 需要的是专用 IAM 用户的长期密钥，并附带这份只读策略，
你随时可以撤销。切勿使用 root 用户的密钥。

## 最低权限
用下面的 JSON 创建一个策略。连接器的后续版本可能需要更多权限，届时本页会列出。

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

要跟踪长时间作业，请为你开启的项目添加相应的语句：

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` 和 `batch:DescribeJobs` 会返回作业的完整描述，其中可能包含它的设置和环境
变量。TaskHub 只读取状态、时间、时间限制和失败原因。

费用（需手动开启）：

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer 是收费的。** AWS 每次请求收费 0.01 美元。TaskHub 每 8 小时最多发出 2 次请求，每月约 1.80 美元，并且会记住
结果，所以重启不会重复付费。读取预算，AWS 没有列出费用。Cost Explorer 需要在
[Billing 控制台](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html)中启用一次；无法通过 API 启用，数据大约一天后才会出现。新账户
在经过一个完整的计费周期之前可能没有预测。在 AWS Organization 中，管理账户可以阻止成员账户使用
Cost Explorer，无论其策略如何设置。

为了读取预算，TaskHub 还会调用一次 `sts:GetCallerIdentity` 来获取账户编号。AWS 对该调用不要求任何权限。该编号只保存在内存中。

`ec2:DescribeRegions` 用于找出你账户中已启用的区域。没有它，TaskHub 只会监视你自己列出的区域，
如果一个都没列出，则只监视 `us-east-1`。

## 创建密钥
1. 在 [IAM 控制台](https://console.aws.amazon.com/iam/)中为 TaskHub 创建一个用户（不需要控制台访问权限），并附加
   上面的策略。
2. 打开 **Users**，选择该用户，打开 **Security credentials** 选项卡。
3. 在 **Access keys** 下选择 **Create access key**。
4. 使用场景选择 **Other**，然后选择 **Next**，再选择 **Create access key**。
5. 选择 **Show**，复制 **Access key ID** 和 **Secret access key** 两部分。AWS 只显示一次秘密访问密钥。

以 `ASIA` 开头的密钥是临时的，TaskHub 会拒绝。请使用以 `AKIA` 开头的密钥。

## 粘贴到 TaskHub
1. TaskHub → **设置** → **连接器** → **AWS**。
2. 打开开关，将访问密钥 ID 和秘密访问密钥粘贴到各自的字段中，点击 **保存**。
3. 状态行应显示 **已连接**。如果 AWS 拒绝该密钥，则不会保存任何内容。

**区域** 留空表示监视账户中已启用的所有区域，也可以只添加你使用的区域。

| 状态行显示 | 含义 |
|---|---|
| 密钥有效，但其策略不允许 `ec2:DescribeRegions` | 添加上面的权限，或列出你的区域 |
| 无权访问此区域 | 策略或账户不允许使用该区域。其他区域不受影响 |
| SageMaker 或 AWS Batch：密钥没有访问权限 | 添加上面的语句，或关闭对应的开关 |
| Cost Explorer：密钥没有访问权限，或未启用 | 添加上面的语句，并在 Billing 控制台中启用 Cost Explorer |
| Cost Explorer：暂无数据 | 刚刚启用：请等待约一天 |
| 请检查这台电脑的时钟 | AWS 会拒绝时间不正确的签名请求。请校正时钟；密钥本身没有问题 |

## 密钥保存在哪里
- 仅保存在 Windows 凭据管理器中，绝不写入 `config.json`。两部分一起保存，之后不会再显示。
- 秘密访问密钥本身从不发送：它只用于为每个请求签名。请求只发往 `ec2.<region>.amazonaws.com`，以及你开启的功能所对应的 `api.sagemaker.<region>.amazonaws.com`、`batch.<region>.amazonaws.com`、`ce.us-east-1.amazonaws.com`、`budgets.amazonaws.com` 和 `sts.amazonaws.com`。

## 撤销
- **在 AWS：** IAM 控制台 → **Users** → 该用户 → **Security credentials** → **Access keys** → **Actions** → **Deactivate**，然后
  **Delete**。
- **在 TaskHub：** 设置 → 连接器 → AWS → **删除**（同时从凭据管理器中删除）。

## 官方文档
- 创建、停用和删除访问密钥：<https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>、
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- 访问密钥和临时凭证：<https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>、
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- 最佳实践：<https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- 调用：<https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>、
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- 长时间作业：<https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>、
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>、
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>、
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>、
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- 费用：<https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>、
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>、
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>、
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>、
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- 区域：<https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
