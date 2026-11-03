# GitHub Actions: token dla TaskHub

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · [Deutsch](github.de.md) · [Français](github.fr.md) · [Italiano](github.it.md) · [Português](github.pt.md) · **Polski** · [中文](github.zh.md) · [日本語](github.ja.md) · [Wszystkie strony](README.md)

## Do czego TaskHub go używa
- **Obserwowanie:** uruchomienia workflow w repozytoriach, które wskażesz — postęp, nieudany krok, wynik.
- **Akcje** (tylko z uprawnieniem do zapisu): Anuluj i Uruchom ponownie uruchomienie.
- **Pull requesty** (opcjonalnie, tylko odczyt): te, które czekają na Twoją recenzję, oraz otwarte pull requesty Dependabota.
- TaskHub nigdy nie zmienia Twoich workflow, kodu ani ustawień repozytorium.

## Utwórz token fine-grained
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (bezpośredni link: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** np. `TaskHub`.
3. **Expiration:** domyślnie 30 dni; wybierz, co Ci odpowiada (TaskHub ostrzeże na tydzień przed wygaśnięciem).
4. **Resource owner:** Ty albo organizacja, do której należą repozytoria.
5. **Repository access:** **Only select repositories** → repozytoria, które wpiszesz w TaskHub.
6. **Permissions** (patrz niżej) → **Generate token** → skopiuj go.

Jeśli właściciel to organizacja wymagająca zatwierdzenia, token czeka w stanie oczekującym, aż zatwierdzi go administrator.

## Minimalne uprawnienia
| Co chcesz | Uprawnienia repozytorium |
|---|---|
| (a) Tylko obserwować | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Anulować / uruchamiać ponownie | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull requesty (opcjonalnie): pull requesty Dependabota wymagają dodatkowo **Pull requests: Read-only** (GitHub pokazuje listę
otwartych PR tylko przy tym uprawnieniu); wyszukiwanie próśb o recenzję nie wymaga żadnego uprawnienia. Token fine-grained widzi prośby o recenzję
tylko w repozytoriach, które mu przyznano, i w publicznych.

Wybierz ten token zamiast drugiej opcji w TaskHub (tokena, który ma już Git Credential Manager) — ten ma
dostęp do wszystkich Twoich repozytoriów.

## Obserwowanie kolejnego repozytorium później
Przy **Only select repositories** token sięga tylko do wybranych repozytoriów. Jeśli TaskHub napisze, że repozytorium
*nie zostało znalezione albo token nie ma do niego dostępu*: sprawdź nazwę, a potem otwórz token na GitHubie (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) i dodaj repozytorium w **Repository access**. GitHub odpowiada tak samo na
błędną nazwę, repozytorium, do którego token nie ma dostępu, a przy niektórych żądaniach także na brak uprawnienia, więc TaskHub nie może powiedzieć, o które z nich chodzi.
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Wklej go do TaskHub
1. TaskHub → **Ustawienia** → **Konektory** → **GitHub Actions**.
2. Włącz konektor, wybierz **Własny token fine-grained (zalecane)**, wklej token, wpisz repozytoria
   (`owner/repo`) i kliknij **Zapisz**.
3. W wierszu powinno być **Połączono jako** *Twój login*. Jeśli GitHub odrzuci token, nic nie zostanie zapisane.

## Gdzie token jest przechowywany
- Tylko w Menedżerze poświadczeń Windows — nigdy w `config.json`.
- Wysyłany tylko do `api.github.com`. Opcjonalny fragment końca logu jest pobierany przez jednorazowy link od GitHuba,
  bez tokena.

## Odwołaj go
- **W GitHubie:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → token →
  **Delete**.
- **W TaskHub:** Ustawienia → Konektory → GitHub Actions → **Usuń** (usuwa go z Menedżera poświadczeń).

## Oficjalna dokumentacja
- Creating a fine-grained token:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Searching pull requests (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Permissions per endpoint:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
