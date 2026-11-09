# AWS: chiave di accesso per TaskHub

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · **Italiano** · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Tutte le pagine](README.md)

## A cosa serve a TaskHub
- **Osservare:** le tue istanze EC2 nelle Region che usi: nome, tipo, Region, stato e da quanto tempo sono in esecuzione.
  Un'istanza in esecuzione da più del limite che imposti (72 ore di default) riceve un promemoria "Dimenticata?".
- **Job lunghi, se li attivi:** job di training SageMaker e job AWS Batch: stato, tempo, motivo del fallimento. Ognuno ha un proprio
  interruttore, disattivato di default. SageMaker non indica una percentuale per i normali job di training, quindi TaskHub non ne mostra una.
- **Costi, se li attivi:** il costo del mese in corso e la previsione da Cost Explorer (AWS addebita queste richieste, vedi sotto) e i tuoi AWS Budgets. Un
  budget oltre il limite genera una notifica al mese.
- **Mai:** TaskHub non avvia, non ferma e non modifica nulla nel tuo account. Questa policy non gliene dà modo.

TaskHub legge ogni Region ogni 10 minuti. La pagina dei prezzi di EC2 di AWS non elenca alcun costo per queste chiamate, ma AWS non dice esplicitamente che siano gratuite.

> Questo connettore è stato costruito dalla documentazione AWS e testato su un simulatore, non ancora su un account AWS reale.

AWS preferisce le credenziali temporanee. TaskHub ha bisogno di una chiave a lungo termine per un utente IAM dedicato con questa policy di sola lettura, e
puoi revocarla in qualsiasi momento. Non usare mai le chiavi dell'utente root.

## Permessi minimi
Crea una policy con questo JSON. Le versioni future del connettore potrebbero richiederne di più, e questa pagina li elencherà.

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

Per seguire i job lunghi, aggiungi gli statement per quelli che attivi:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` e `batch:DescribeJobs` restituiscono l'intera descrizione di un job, che può includere le sue impostazioni e le variabili
d'ambiente. TaskHub legge solo lo stato, i tempi, il limite di tempo e il motivo del fallimento.

Costi, se li attivi:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer è a pagamento.** AWS addebita $0.01 per richiesta. TaskHub ne fa 2 al massimo ogni 8 ore, circa $1.80 al mese, e ricorda la
risposta così un riavvio non la paga di nuovo. AWS non elenca alcun costo per la lettura dei budget. Cost Explorer va abilitato una volta nella
[console Billing](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html); non si può abilitare tramite l'API, e i dati compaiono entro circa
un giorno. Un account nuovo può non avere previsioni finché non ha completato un ciclo di fatturazione. In una AWS Organization, l'account di gestione può bloccare
l'accesso a Cost Explorer agli account membri, qualunque sia la loro policy.

Per leggere i budget TaskHub chiede anche a `sts:GetCallerIdentity` il numero dell'account, una volta. AWS non richiede alcun permesso per quella chiamata. Il numero resta in memoria.

`ec2:DescribeRegions` trova le Region abilitate nel tuo account. Senza, TaskHub osserva solo le Region che elenchi
tu, oppure `us-east-1` se non ne elenchi nessuna.

## Crea la chiave
1. Nella [console IAM](https://console.aws.amazon.com/iam/), crea un utente per TaskHub (non serve l'accesso alla console) e allega
   la policy qui sopra.
2. Apri **Users**, seleziona l'utente e apri la scheda **Security credentials**.
3. In **Access keys**, scegli **Create access key**.
4. Scegli **Other** come caso d'uso, poi **Next**, poi **Create access key**.
5. Scegli **Show** e copia sia l'**Access key ID** sia la **Secret access key**. AWS mostra il secret una sola volta.

Le chiavi che iniziano con `ASIA` sono temporanee e TaskHub le rifiuta. Usa una chiave che inizia con `AKIA`.

## Incollala in TaskHub
1. TaskHub → **Impostazioni** → **Connettori** → **AWS**.
2. Attivalo, incolla l'ID della chiave di accesso e il secret nei rispettivi campi e clicca **Salva**.
3. La riga deve dire **Connesso**. Se AWS rifiuta la chiave, non viene salvato nulla.

Lascia **Regioni** vuoto per osservare tutte le Region abilitate nell'account, oppure aggiungi solo quelle che usi.

| La riga dice | Cosa significa |
|---|---|
| La chiave funziona, ma la sua policy non consente `ec2:DescribeRegions` | Aggiungi il permesso qui sopra oppure elenca le tue Region |
| Nessun accesso in questa Region | La policy o l'account non consente questa Region. Le altre Region continuano a funzionare |
| SageMaker o AWS Batch: la chiave non ha accesso | Aggiungi gli statement qui sopra, oppure disattiva quell'interruttore |
| Cost Explorer: la chiave non ha accesso, oppure non è abilitato | Aggiungi lo statement qui sopra e abilita Cost Explorer nella console Billing |
| Cost Explorer: ancora nessun dato | Abilitato da poco: aspetta circa un giorno |
| Controlla l'orologio di questo computer | AWS rifiuta le richieste firmate con un'ora sbagliata. Correggi l'orologio; la chiave va bene |

## Dove viene conservata la chiave
- Solo in Gestione credenziali di Windows, mai in `config.json`. Le due parti sono conservate insieme e non vengono mai più mostrate.
- Il secret in sé non viene mai inviato: firma ogni richiesta. Le richieste vanno solo a `ec2.<region>.amazonaws.com` e, per ciò che attivi, a `api.sagemaker.<region>.amazonaws.com` e `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` e `sts.amazonaws.com`.

## Revocala
- **Su AWS:** console IAM → **Users** → l'utente → **Security credentials** → **Access keys** → **Actions** → **Deactivate**, poi
  **Delete**.
- **In TaskHub:** Impostazioni → Connettori → AWS → **Rimuovi** (la elimina da Gestione credenziali).

## Documentazione ufficiale
- Creare, disattivare ed eliminare le chiavi di accesso: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Chiavi di accesso e credenziali temporanee: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Best practice: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- Le chiamate: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Job lunghi: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Costi: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Region: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
