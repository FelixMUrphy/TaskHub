# AWS: klucz dostępu dla TaskHub

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · **Polski** · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Wszystkie strony](README.md)

## Do czego TaskHub go używa
- **Obserwowanie:** Twoje instancje EC2 w regionach, z których korzystasz: nazwa, typ, region, stan i czas działania.
  Instancja działająca dłużej niż ustawiony przez Ciebie limit (domyślnie 72 godziny) dostaje przypomnienie „Zapomniana?”.
- **Długie zadania, jeśli je włączysz:** zadania treningowe SageMaker i zadania AWS Batch: stan, czas, przyczyna błędu. Każde ma własny
  przełącznik, domyślnie wyłączony. SageMaker nie podaje procentów dla zwykłych zadań treningowych, więc TaskHub ich nie pokazuje.
- **Koszty, jeśli je włączysz:** koszt i prognoza za bieżący miesiąc z Cost Explorer (AWS pobiera opłatę za te żądania, patrz niżej) oraz Twoje AWS Budgets. Budżet
  przekroczony ponad limit dostaje jedno powiadomienie w miesiącu.
- **Nigdy:** TaskHub niczego nie uruchamia, nie zatrzymuje ani nie zmienia na Twoim koncie. Te zasady nie dają mu takiej możliwości.

TaskHub odczytuje każdy region co 10 minut. Strona cennika EC2 w AWS nie wymienia opłat za te wywołania, ale AWS nie mówi wprost, że są darmowe.

> Ten konektor powstał na podstawie dokumentacji AWS i został przetestowany na symulatorze, jeszcze nie na prawdziwym koncie AWS.

AWS woli poświadczenia tymczasowe. TaskHub potrzebuje klucza długoterminowego dla dedykowanego użytkownika IAM z tą polityką tylko do odczytu, a Ty
możesz go odwołać w każdej chwili. Nigdy nie używaj kluczy użytkownika root.

## Minimalne uprawnienia
Utwórz politykę z tym JSON-em. Późniejsze wersje konektora mogą wymagać więcej — ta strona to wymieni.

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

Aby śledzić długie zadania, dodaj instrukcje dla tych, które włączysz:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` i `batch:DescribeJobs` zwracają pełny opis zadania, który może zawierać jego ustawienia i zmienne
środowiskowe. TaskHub czyta tylko stan, czasy, limit czasu i przyczynę błędu.

Koszty, jeśli je włączysz:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer jest płatny.** AWS pobiera 0,01 $ za żądanie. TaskHub wysyła najwyżej 2 co 8 godzin, czyli około 1,80 $ miesięcznie, i zapamiętuje
odpowiedź, żeby po restarcie nie płacić ponownie. AWS nie wymienia opłaty za odczyt budżetów. Cost Explorer trzeba raz włączyć w
[konsoli Billing](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html); nie da się go włączyć przez API, a dane pojawiają się po około
dobie. Nowe konto może nie mieć prognozy, dopóki nie minie pełny okres rozliczeniowy. W AWS Organization konto zarządzające może zablokować kontom członkowskim
dostęp do Cost Explorer, niezależnie od ich polityki.

Aby odczytać budżety, TaskHub pyta też `sts:GetCallerIdentity` o numer konta, raz. AWS nie wymaga do tego wywołania żadnego uprawnienia. Numer zostaje w pamięci.

`ec2:DescribeRegions` znajduje regiony włączone na Twoim koncie. Bez tego TaskHub obserwuje tylko regiony, które sam wpiszesz,
albo `us-east-1`, jeśli nie wpiszesz żadnego.

## Utwórz klucz
1. W [konsoli IAM](https://console.aws.amazon.com/iam/) utwórz użytkownika dla TaskHub (dostęp do konsoli nie jest potrzebny) i dołącz
   powyższą politykę.
2. Otwórz **Users**, wybierz użytkownika i otwórz kartę **Security credentials**.
3. W sekcji **Access keys** wybierz **Create access key**.
4. Jako przypadek użycia wybierz **Other**, potem **Next**, potem **Create access key**.
5. Wybierz **Show** i skopiuj zarówno **Access key ID**, jak i **Secret access key**. AWS pokazuje sekret tylko raz.

Klucze zaczynające się od `ASIA` są tymczasowe i TaskHub je odrzuca. Użyj klucza zaczynającego się od `AKIA`.

## Wklej go do TaskHub
1. TaskHub → **Ustawienia** → **Konektory** → **AWS**.
2. Włącz konektor, wklej identyfikator klucza dostępu i sekret w odpowiednie pola, kliknij **Zapisz**.
3. W wierszu powinno być **Połączono**. Jeśli AWS odrzuci klucz, nic nie zostanie zapisane.

Zostaw **Regiony** puste, aby obserwować każdy region włączony na koncie, albo dodaj tylko te, z których korzystasz.

| W wierszu jest | Co to znaczy |
|---|---|
| Klucz działa, ale jego zasady nie zezwalają na `ec2:DescribeRegions` | Dodaj powyższe uprawnienie albo podaj swoje regiony |
| Brak dostępu w tym regionie | Polityka lub konto nie zezwala na ten region. Pozostałe regiony działają dalej |
| SageMaker lub AWS Batch: klucz nie ma dostępu | Dodaj powyższe instrukcje albo wyłącz ten przełącznik |
| Cost Explorer: klucz nie ma dostępu albo nie jest włączony | Dodaj powyższą instrukcję i włącz Cost Explorer w konsoli Billing |
| Cost Explorer: brak danych | Włączony niedawno: poczekaj około doby |
| Sprawdź zegar tego komputera | AWS odrzuca żądania podpisane błędnym czasem. Popraw zegar; klucz jest w porządku |

## Gdzie klucz jest przechowywany
- Tylko w Menedżerze poświadczeń Windows, nigdy w `config.json`. Obie części są przechowywane razem i nigdy więcej nie są pokazywane.
- Sam sekret nigdy nie jest wysyłany: podpisuje każde żądanie. Żądania trafiają tylko do `ec2.<region>.amazonaws.com` oraz, dla tego, co włączysz, do `api.sagemaker.<region>.amazonaws.com` i `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` i `sts.amazonaws.com`.

## Odwołaj go
- **W AWS:** konsola IAM → **Users** → użytkownik → **Security credentials** → **Access keys** → **Actions** → **Deactivate**, a potem
  **Delete**.
- **W TaskHub:** Ustawienia → Konektory → AWS → **Usuń** (usuwa go z Menedżera poświadczeń).

## Oficjalna dokumentacja
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
