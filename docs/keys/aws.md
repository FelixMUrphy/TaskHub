# AWS: access key for TaskHub

**English** · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [All pages](README.md)

## What TaskHub uses it for
- **Watch:** your EC2 instances in the Regions you use: name, type, Region, state and how long they have been running.
  An instance running longer than the limit you set (72 hours by default) gets a "Forgotten?" reminder.
- **Long jobs, if you turn them on:** SageMaker training jobs and AWS Batch jobs: state, time, failure reason. Each has its own
  switch, off by default. SageMaker doesn't report a percentage for ordinary training jobs, so TaskHub doesn't show one.
- **Costs, if you turn them on:** this month's cost and forecast from Cost Explorer (AWS charges for these requests, see below) and your AWS Budgets. A
  budget over its limit gets one notification a month.
- **Never:** TaskHub starts, stops or changes nothing in your account. This policy gives it no way to.

TaskHub reads each Region every 10 minutes. AWS's EC2 pricing page lists no charge for these calls, but AWS doesn't say outright that they are free.

> This connector was built from the AWS documentation and tested against a simulator, not yet on a real AWS account.

AWS prefers temporary credentials. TaskHub needs a long-term key for a dedicated IAM user with this read-only policy, and you
can revoke it at any time. Never use the root user's keys.

## Minimum permissions
Create a policy with this JSON. Later versions of the connector may need more, and this page will list them.

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

To follow long jobs, add the statements for the ones you turn on:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` and `batch:DescribeJobs` return a job's whole description, which can include its settings and environment
variables. TaskHub reads only the state, times, time limit and failure reason.

Costs, if you turn them on:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer is paid.** AWS charges $0.01 per request. TaskHub makes 2 at most every 8 hours, about $1.80 a month, and remembers the
answer so a restart doesn't pay again. AWS doesn't list a charge for reading budgets. Cost Explorer has to be enabled once in the
[Billing console](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html); it can't be enabled through the API, and data appears within about
a day. A new account may have no forecast until it has a full billing cycle. In an AWS Organization, the management account can block member accounts from
Cost Explorer whatever their policy says.

To read budgets TaskHub also asks `sts:GetCallerIdentity` for the account number once. AWS requires no permission for that call. The number stays in memory.

`ec2:DescribeRegions` finds the Regions enabled in your account. Without it, TaskHub watches only the Regions you list
yourself, or `us-east-1` if you list none.

## Create the key
1. In the [IAM console](https://console.aws.amazon.com/iam/), create a user for TaskHub (no console access needed) and attach
   the policy above.
2. Open **Users**, select the user, open the **Security credentials** tab.
3. Under **Access keys**, choose **Create access key**.
4. Choose **Other** as the use case, then **Next**, then **Create access key**.
5. Choose **Show** and copy both the **Access key ID** and the **Secret access key**. AWS shows the secret only once.

Keys starting with `ASIA` are temporary and TaskHub refuses them. Use a key starting with `AKIA`.

## Paste it into TaskHub
1. TaskHub → **Settings** → **Connectors** → **AWS**.
2. Turn it on, paste the access key ID and the secret into their fields, click **Save**.
3. The line should read **Connected**. If AWS rejects the key, nothing is saved.

Leave **Regions** empty to watch every Region enabled in the account, or add only the ones you use.

| The line says | What it means |
|---|---|
| The key works, but its policy doesn't allow `ec2:DescribeRegions` | Add the permission above or list your Regions |
| No access in this Region | The policy or the account doesn't allow this Region. Other Regions keep working |
| SageMaker or AWS Batch: the key has no access | Add the statements above, or turn that switch off |
| Cost Explorer: the key has no access, or it isn't enabled | Add the statement above, and enable Cost Explorer in the Billing console |
| Cost Explorer: no data yet | Enabled recently: allow about a day |
| Check this computer's clock | AWS refuses requests signed with a wrong time. Fix the clock; the key is fine |

## Where the key is kept
- Only in the Windows Credential Manager, never in `config.json`. Both parts are kept together and never shown again.
- The secret itself is never sent: it signs each request. Requests go only to `ec2.<region>.amazonaws.com` and, for what you turn on, `api.sagemaker.<region>.amazonaws.com` and `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` and `sts.amazonaws.com`.

## Revoke it
- **At AWS:** IAM console → **Users** → the user → **Security credentials** → **Access keys** → **Actions** → **Deactivate**, then
  **Delete**.
- **In TaskHub:** Settings → Connectors → AWS → **Remove** (deletes it from the Credential Manager).

## Official docs
- Create, deactivate and delete access keys: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Access keys and temporary credentials: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Best practices: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- The calls: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Long jobs: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Costs: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Regions: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
