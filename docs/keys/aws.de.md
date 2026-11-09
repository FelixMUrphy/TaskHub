# AWS: Zugriffsschlüssel für TaskHub

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · **Deutsch** · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Alle Seiten](README.md)

## Wofür TaskHub ihn braucht
- **Beobachten:** deine EC2-Instanzen in den Regionen, die du nutzt: Name, Typ, Region, Status und wie lange sie schon laufen.
  Eine Instanz, die länger läuft als das von dir gesetzte Limit (standardmäßig 72 Stunden), bekommt eine Erinnerung „Vergessen?“.
- **Lange Jobs, wenn du sie einschaltest:** SageMaker-Trainingsjobs und AWS-Batch-Jobs: Status, Zeit, Fehlergrund. Jeder hat seinen eigenen
  Schalter, standardmäßig aus. SageMaker meldet für gewöhnliche Trainingsjobs keinen Prozentwert, deshalb zeigt TaskHub keinen.
- **Kosten, wenn du sie einschaltest:** die Kosten und die Prognose dieses Monats aus Cost Explorer (AWS berechnet diese Anfragen, siehe unten) und deine AWS Budgets. Ein
  Budget über seinem Limit löst einmal im Monat eine Benachrichtigung aus.
- **Nie:** TaskHub startet, stoppt oder ändert nichts in deinem Konto. Diese Richtlinie gibt ihm keine Möglichkeit dazu.

TaskHub liest jede Region alle 10 Minuten. Die EC2-Preisseite von AWS nennt keine Gebühr für diese Aufrufe, aber AWS sagt nicht ausdrücklich, dass sie kostenlos sind.

> Dieser Konnektor wurde nach der AWS-Dokumentation gebaut und gegen einen Simulator getestet, noch nicht mit einem echten AWS-Konto.

AWS bevorzugt temporäre Zugangsdaten. TaskHub braucht einen langfristigen Schlüssel für einen eigenen IAM-Benutzer mit dieser Nur-Lese-Richtlinie, und du
kannst ihn jederzeit widerrufen. Nutze nie die Schlüssel des Root-Benutzers.

## Minimale Berechtigungen
Erstelle eine Richtlinie mit diesem JSON. Spätere Versionen des Konnektors brauchen vielleicht mehr, und diese Seite wird es auflisten.

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

Um lange Jobs zu verfolgen, füge die Statements für die hinzu, die du einschaltest:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` und `batch:DescribeJobs` liefern die gesamte Beschreibung eines Jobs, die seine Einstellungen und Umgebungsvariablen
enthalten kann. TaskHub liest nur den Status, die Zeiten, das Zeitlimit und den Fehlergrund.

Kosten, wenn du sie einschaltest:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer ist kostenpflichtig.** AWS berechnet 0,01 $ pro Anfrage. TaskHub stellt höchstens 2 alle 8 Stunden, etwa 1,80 $ im Monat, und merkt sich die
Antwort, damit ein Neustart nicht noch einmal zahlt. AWS nennt keine Gebühr für das Lesen von Budgets. Cost Explorer muss einmal in der
[Billing-Konsole](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html) aktiviert werden; das geht nicht über die API, und Daten erscheinen nach etwa
einem Tag. Ein neues Konto hat vielleicht keine Prognose, bis es einen vollen Abrechnungszyklus hinter sich hat. In einer AWS Organization kann das Verwaltungskonto Mitgliedskonten
den Zugriff auf Cost Explorer sperren, egal was ihre Richtlinie sagt.

Um Budgets zu lesen, fragt TaskHub außerdem einmal mit `sts:GetCallerIdentity` nach der Kontonummer. AWS verlangt dafür keine Berechtigung. Die Nummer bleibt im Arbeitsspeicher.

`ec2:DescribeRegions` findet die Regionen, die in deinem Konto aktiviert sind. Ohne es beobachtet TaskHub nur die Regionen, die du selbst angibst,
oder `us-east-1`, wenn du keine angibst.

## Schlüssel erstellen
1. Erstelle in der [IAM-Konsole](https://console.aws.amazon.com/iam/) einen Benutzer für TaskHub (ein Konsolenzugang ist nicht nötig) und weise ihm
   die obige Richtlinie zu.
2. Öffne **Users**, wähle den Benutzer und öffne den Tab **Security credentials**.
3. Wähle unter **Access keys** die Option **Create access key**.
4. Wähle **Other** als Anwendungsfall, dann **Next**, dann **Create access key**.
5. Wähle **Show** und kopiere sowohl die **Access key ID** als auch den **Secret access key**. AWS zeigt das Secret nur einmal an.

Schlüssel, die mit `ASIA` beginnen, sind temporär, und TaskHub lehnt sie ab. Nimm einen Schlüssel, der mit `AKIA` beginnt.

## In TaskHub einfügen
1. TaskHub → **Einstellungen** → **Konnektoren** → **AWS**.
2. Schalte ihn ein, füge die Access Key ID und das Secret in ihre Felder ein und klicke auf **Speichern**.
3. Die Zeile sollte **Verbunden** lauten. Wenn AWS den Schlüssel ablehnt, wird nichts gespeichert.

Lass **Regionen** leer, um jede im Konto aktivierte Region zu beobachten, oder trage nur die ein, die du nutzt.

| Die Zeile sagt | Was es bedeutet |
|---|---|
| Der Schlüssel funktioniert, aber seine Richtlinie erlaubt `ec2:DescribeRegions` nicht | Ergänze die obige Berechtigung oder gib deine Regionen an |
| Kein Zugriff in dieser Region | Die Richtlinie oder das Konto erlaubt diese Region nicht. Die anderen Regionen funktionieren weiter |
| SageMaker oder AWS Batch: Der Schlüssel hat keinen Zugriff | Ergänze die obigen Statements oder schalte diesen Schalter aus |
| Cost Explorer: Der Schlüssel hat keinen Zugriff, oder es ist nicht aktiviert | Ergänze das obige Statement und aktiviere Cost Explorer in der Billing-Konsole |
| Cost Explorer: noch keine Daten | Kürzlich aktiviert: Warte etwa einen Tag |
| Prüfe die Uhr dieses Computers | AWS lehnt Anfragen ab, die mit falscher Uhrzeit signiert sind. Korrigiere die Uhr; der Schlüssel ist in Ordnung |

## Wo der Schlüssel gespeichert wird
- Nur in der Windows-Anmeldeinformationsverwaltung, nie in `config.json`. Beide Teile werden zusammen gespeichert und nie wieder angezeigt.
- Das Secret selbst wird nie gesendet: Es signiert jede Anfrage. Anfragen gehen nur an `ec2.<region>.amazonaws.com` und, für das, was du einschaltest, an `api.sagemaker.<region>.amazonaws.com` und `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` und `sts.amazonaws.com`.

## Widerrufen
- **Bei AWS:** IAM-Konsole → **Users** → der Benutzer → **Security credentials** → **Access keys** → **Actions** → **Deactivate**, dann
  **Delete**.
- **In TaskHub:** Einstellungen → Konnektoren → AWS → **Entfernen** (löscht ihn aus der Anmeldeinformationsverwaltung).

## Offizielle Dokumentation
- Zugriffsschlüssel erstellen, deaktivieren und löschen: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Zugriffsschlüssel und temporäre Zugangsdaten: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Best Practices: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- Die Aufrufe: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Lange Jobs: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Kosten: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Regionen: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
