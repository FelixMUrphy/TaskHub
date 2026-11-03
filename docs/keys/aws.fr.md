# AWS : clé d'accès pour TaskHub

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · **Français** · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Toutes les pages](README.md)

## À quoi TaskHub l'utilise
- **Surveiller :** vos instances EC2 dans les Regions que vous utilisez : nom, type, Region, état et depuis combien de temps elles tournent.
  Une instance active depuis plus longtemps que la limite que vous fixez (72 heures par défaut) reçoit un rappel « Oubliée ? ».
- **Travaux longs, si vous les activez :** travaux d'entraînement SageMaker et travaux AWS Batch : état, durée, cause de l'échec. Chacun a son propre
  interrupteur, désactivé par défaut. SageMaker n'indique pas de pourcentage pour les travaux d'entraînement ordinaires, TaskHub n'en affiche donc pas.
- **Coûts, si vous les activez :** le coût du mois et la prévision depuis Cost Explorer (AWS facture ces requêtes, voir ci-dessous) et vos AWS Budgets. Un
  budget qui dépasse sa limite déclenche une notification par mois.
- **Jamais :** TaskHub ne démarre, n'arrête ni ne modifie rien dans votre compte. Cette stratégie ne lui en donne pas le moyen.

TaskHub lit chaque Region toutes les 10 minutes. La page de tarifs EC2 d'AWS n'indique aucun frais pour ces appels, mais AWS ne dit pas explicitement qu'ils sont gratuits.

> Ce connecteur a été conçu d'après la documentation AWS et testé sur un simulateur, pas encore sur un vrai compte AWS.

AWS préfère les identifiants temporaires. TaskHub a besoin d'une clé à long terme pour un utilisateur IAM dédié, avec cette stratégie en lecture seule, et vous
pouvez la révoquer à tout moment. N'utilisez jamais les clés de l'utilisateur root.

## Autorisations minimales
Créez une stratégie avec ce JSON. Les versions ultérieures du connecteur pourront demander plus, et cette page les indiquera.

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

Pour suivre les travaux longs, ajoutez les déclarations correspondant à ceux que vous activez :

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` et `batch:DescribeJobs` renvoient la description complète d'un travail, qui peut inclure ses paramètres et ses variables
d'environnement. TaskHub ne lit que l'état, les heures, la limite de temps et la cause de l'échec.

Coûts, si vous les activez :

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer est payant.** AWS facture 0,01 $ par requête. TaskHub en fait 2 au plus toutes les 8 heures, soit environ 1,80 $ par mois, et mémorise la
réponse pour qu'un redémarrage ne coûte pas une nouvelle fois. AWS n'indique aucun frais pour la lecture des budgets. Cost Explorer doit être activé une fois dans la
[console Billing](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html) ; il ne peut pas être activé via l'API, et les données apparaissent sous un jour
environ. Un nouveau compte peut ne pas avoir de prévision avant un cycle de facturation complet. Dans une AWS Organization, le compte de gestion peut bloquer l'accès des comptes membres à
Cost Explorer, quelle que soit leur stratégie.

Pour lire les budgets, TaskHub demande aussi une fois `sts:GetCallerIdentity` pour obtenir le numéro de compte. AWS n'exige aucune autorisation pour cet appel. Le numéro reste en mémoire.

`ec2:DescribeRegions` trouve les Regions activées dans votre compte. Sans elle, TaskHub ne surveille que les Regions que vous indiquez
vous-même, ou `us-east-1` si vous n'en indiquez aucune.

## Créer la clé
1. Dans la [console IAM](https://console.aws.amazon.com/iam/), créez un utilisateur pour TaskHub (aucun accès à la console n'est nécessaire) et attachez-lui
   la stratégie ci-dessus.
2. Ouvrez **Users**, sélectionnez l'utilisateur, ouvrez l'onglet **Security credentials**.
3. Sous **Access keys**, choisissez **Create access key**.
4. Choisissez **Other** comme cas d'usage, puis **Next**, puis **Create access key**.
5. Choisissez **Show** et copiez à la fois l'**Access key ID** et la **Secret access key**. AWS n'affiche le secret qu'une seule fois.

Les clés qui commencent par `ASIA` sont temporaires et TaskHub les refuse. Utilisez une clé qui commence par `AKIA`.

## La coller dans TaskHub
1. TaskHub → **Paramètres** → **Connecteurs** → **AWS**.
2. Activez-le, collez l'ID de clé d'accès et le secret dans leurs champs, cliquez sur **Enregistrer**.
3. La ligne doit indiquer **Connecté**. Si AWS refuse la clé, rien n'est enregistré.

Laissez **Régions** vide pour surveiller toutes les Regions activées dans le compte, ou n'ajoutez que celles que vous utilisez.

| La ligne indique | Ce que cela signifie |
|---|---|
| La clé fonctionne, mais sa stratégie n’autorise pas `ec2:DescribeRegions` | Ajoutez l'autorisation ci-dessus ou indiquez vos Regions |
| Pas d’accès dans cette région | La stratégie ou le compte n'autorise pas cette Region. Les autres Regions continuent de fonctionner |
| SageMaker ou AWS Batch : la clé n’a pas accès | Ajoutez les déclarations ci-dessus, ou désactivez cet interrupteur |
| Cost Explorer : la clé n’a pas accès, ou il n’est pas activé | Ajoutez la déclaration ci-dessus et activez Cost Explorer dans la console Billing |
| Cost Explorer : pas encore de données | Activé récemment : comptez environ un jour |
| Vérifiez l’horloge de cet ordinateur | AWS refuse les requêtes signées avec une heure erronée. Corrigez l'horloge ; la clé est bonne |

## Où la clé est conservée
- Uniquement dans le Gestionnaire d'identification Windows, jamais dans `config.json`. Les deux parties sont conservées ensemble et ne sont plus jamais affichées.
- Le secret lui-même n'est jamais envoyé : il sert à signer chaque requête. Les requêtes vont uniquement à `ec2.<region>.amazonaws.com` et, pour ce que vous activez, `api.sagemaker.<region>.amazonaws.com` et `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` et `sts.amazonaws.com`.

## La révoquer
- **Chez AWS :** console IAM → **Users** → l'utilisateur → **Security credentials** → **Access keys** → **Actions** → **Deactivate**, puis
  **Delete**.
- **Dans TaskHub :** Paramètres → Connecteurs → AWS → **Supprimer** (la retire du Gestionnaire d'identification).

## Documentation officielle
- Créer, désactiver et supprimer des clés d'accès : <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Clés d'accès et identifiants temporaires : <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Bonnes pratiques : <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- Les appels : <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Travaux longs : <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Coûts : <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Regions : <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
