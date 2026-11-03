# AWS: ключ доступу для TaskHub

[English](aws.md) · **Українська** · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Усі сторінки](README.md)

## Для чого він TaskHub
- **Спостереження:** ваші інстанси EC2 у регіонах, якими ви користуєтеся: назва, тип, регіон, стан і час роботи.
  Інстанс, що працює довше за встановлений вами ліміт (типово 72 години), отримує нагадування «Забули?».
- **Довгі завдання, якщо ви їх увімкнете:** завдання навчання SageMaker і завдання AWS Batch: стан, час, причина збою. Кожне має свій
  перемикач, типово вимкнений. Для звичайного навчання SageMaker не повідомляє відсотків, тому TaskHub їх не показує.
- **Витрати, якщо ви їх увімкнете:** витрати за місяць і прогноз із Cost Explorer (AWS бере плату за ці запити, див. нижче) і ваші AWS Budgets. Бюджет
  понад ліміт дає одне сповіщення на місяць.
- **Ніколи:** TaskHub нічого не запускає, не зупиняє і не змінює у вашому обліковому записі. Ця політика не дає йому такої змоги.

TaskHub читає кожен регіон раз на 10 хвилин. На сторінці цін EC2 немає плати за такі виклики, але прямо про їхню безплатність AWS не пише.

> Цей конектор створено за документацією AWS і перевірено на симуляторі, але ще не на справжньому обліковому записі AWS.

AWS радить тимчасові облікові дані. TaskHub потрібен довгостроковий ключ окремого користувача IAM із цією політикою лише для
читання, і ви можете відкликати його будь-коли. Ніколи не використовуйте ключі root-користувача.

## Мінімальні дозволи
Створіть політику з таким JSON. Наступним версіям конектора може знадобитися більше, і ця сторінка їх перелічить.

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

Щоб стежити за довгими завданнями, додайте записи для тих, які вмикаєте:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` і `batch:DescribeJobs` повертають повний опис завдання, де можуть бути його параметри та змінні середовища.
TaskHub читає лише стан, час, ліміт часу й причину збою.

Витрати, якщо ви їх увімкнете:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer платний.** AWS бере $0.01 за запит. TaskHub робить 2 запити не частіше ніж раз на 8 годин, приблизно $1.80 на місяць, і запам’ятовує
відповідь, тож перезапуск не коштує нічого додатково. За читання бюджетів AWS плати не вказує. Cost Explorer треба один раз увімкнути в
[консолі Billing](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html); через API це неможливо, а дані з’являються приблизно за добу.
Новий акаунт може не мати прогнозу, доки не мине повний розрахунковий період. В AWS Organization керівний акаунт може заблокувати Cost Explorer для
інших акаунтів незалежно від їхньої політики.

Щоб читати бюджети, TaskHub один раз запитує номер акаунта через `sts:GetCallerIdentity`. AWS не вимагає для цього дозволу. Номер лишається в пам’яті.

`ec2:DescribeRegions` знаходить регіони, увімкнені в обліковому записі. Без нього TaskHub стежить лише за регіонами, які ви
вказали самі, або за `us-east-1`, якщо ви не вказали жодного.

## Створіть ключ
1. У [консолі IAM](https://console.aws.amazon.com/iam/) створіть користувача для TaskHub (доступ до консолі не потрібен) і
   додайте йому політику вище.
2. Відкрийте **Users**, виберіть користувача, відкрийте вкладку **Security credentials**.
3. У розділі **Access keys** виберіть **Create access key**.
4. Як варіант використання виберіть **Other**, далі **Next**, далі **Create access key**.
5. Виберіть **Show** і скопіюйте **Access key ID** та **Secret access key**. AWS показує секрет лише один раз.

Ключі, що починаються з `ASIA`, тимчасові, і TaskHub їх не приймає. Використовуйте ключ, що починається з `AKIA`.

## Вставте його в TaskHub
1. TaskHub → **Налаштування** → **Конектори** → **AWS**.
2. Увімкніть, вставте ID ключа і секрет у їхні поля, натисніть **Зберегти**.
3. У рядку має бути **Під’єднано**. Якщо AWS відхилить ключ, нічого не буде збережено.

Залиште **Регіони** порожнім, щоб стежити за всіма регіонами, увімкненими в обліковому записі, або додайте лише потрібні.

| У рядку написано | Що це означає |
|---|---|
| Ключ працює, але його політика не дозволяє `ec2:DescribeRegions` | Додайте дозвіл вище або вкажіть свої регіони |
| Немає доступу в цьому регіоні | Політика або обліковий запис не дозволяє цей регіон. Інші регіони працюють далі |
| SageMaker або AWS Batch: ключ не має доступу | Додайте записи вище або вимкніть цей перемикач |
| Cost Explorer: ключ не має доступу, або його не ввімкнено | Додайте запис вище й увімкніть Cost Explorer у консолі Billing |
| Cost Explorer: даних ще немає | Щойно ввімкнено: зачекайте приблизно добу |
| Перевірте годинник цього комп’ютера | AWS відхиляє запити, підписані з неправильним часом. Виправте годинник; ключ справний |

## Де зберігається ключ
- Лише у Диспетчері облікових даних Windows, ніколи в `config.json`. Обидві частини зберігаються разом і більше не показуються.
- Сам секрет ніколи не надсилається: він підписує кожен запит. Запити йдуть лише на `ec2.<region>.amazonaws.com` і, для увімкненого, на `api.sagemaker.<region>.amazonaws.com` та `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` і `sts.amazonaws.com`.

## Відкличте його
- **В AWS:** консоль IAM → **Users** → користувач → **Security credentials** → **Access keys** → **Actions** → **Deactivate**,
  потім **Delete**.
- **У TaskHub:** Налаштування → Конектори → AWS → **Видалити** (видаляє його з Диспетчера облікових даних).

## Офіційна документація
- Створення, вимкнення і видалення ключів доступу: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Ключі доступу і тимчасові облікові дані: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Найкращі практики: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- Виклики: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Довгі завдання: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Витрати: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Регіони: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
