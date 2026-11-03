# GitHub Actions: Token für TaskHub

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · **Deutsch** · [Français](github.fr.md) · [Italiano](github.it.md) · [Português](github.pt.md) · [Polski](github.pl.md) · [中文](github.zh.md) · [日本語](github.ja.md) · [Alle Seiten](README.md)

## Wofür TaskHub ihn braucht
- **Beobachten:** Workflow-Läufe der Repositories, die du angibst – Fortschritt, der fehlgeschlagene Schritt, das Ergebnis.
- **Aktionen** (nur mit Schreibzugriff): Lauf abbrechen und Lauf neu starten.
- **Pull Requests** (optional, nur lesend): die, die auf dein Review warten, und offene Dependabot-Pull-Requests.
- TaskHub ändert nie deine Workflows, deinen Code oder die Repository-Einstellungen.

## Fine-grained Token erstellen
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (direkter Link: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** z. B. `TaskHub`.
3. **Expiration:** standardmäßig 30 Tage; wähle, was zu dir passt (TaskHub warnt eine Woche vor dem Ablauf).
4. **Resource owner:** du selbst oder die Organisation, der die Repositories gehören.
5. **Repository access:** **Only select repositories** → die Repositories, die du in TaskHub angeben wirst.
6. **Permissions** (siehe unten) → **Generate token** → kopiere es.

Wenn der Eigentümer eine Organisation ist, die eine Freigabe verlangt, bleibt das Token ausstehend, bis ein Admin es freigibt.

## Minimale Berechtigungen
| Du willst | Repository permissions |
|---|---|
| (a) Nur beobachten | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Abbrechen / Neu starten | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull Requests (optional): Für Dependabot-Pull-Requests brauchst du zusätzlich **Pull requests: Read-only** (GitHub zeigt die Liste
der offenen PRs nur mit dieser Berechtigung); die Suche nach Review-Anfragen braucht keine Berechtigung. Ein Fine-grained Token sieht Review-Anfragen
nur in den Repositories, die ihm zugewiesen wurden, und in öffentlichen.

Nimm dieses Token statt der anderen Option von TaskHub (dem Token, das der Git Credential Manager schon hat); diese hat
Zugriff auf alle deine Repositories.

## Später ein weiteres Repository beobachten
Mit **Only select repositories** erreicht das Token nur die Repositories, die du gewählt hast. Wenn TaskHub meldet, ein Repository sei
*nicht gefunden, oder das Token hat keinen Zugriff*: Prüfe den Namen, öffne dann das Token bei GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) und füge das Repository unter **Repository access** hinzu. GitHub antwortet gleich bei
einem falschen Namen, bei einem Repository, das dem Token nicht zugewiesen wurde, und bei manchen Anfragen auch bei einer fehlenden Berechtigung; TaskHub kann dir also nicht sagen, welcher Fall es ist.
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## In TaskHub einfügen
1. TaskHub → **Einstellungen** → **Konnektoren** → **GitHub Actions**.
2. Schalte ihn ein, wähle **Eigener Fine-grained-Token (empfohlen)**, füge das Token ein, gib die Repositories an
   (`owner/repo`) und klicke auf **Speichern**.
3. Die Zeile sollte **Verbunden als** *dein Login* lauten. Wenn GitHub das Token ablehnt, wird nichts gespeichert.

## Wo das Token gespeichert wird
- Nur in der Windows-Anmeldeinformationsverwaltung – nie in `config.json`.
- Es wird nur an `api.github.com` gesendet. Das optionale Ende des Logs wird über den einmaligen Link abgerufen, den GitHub gibt,
  ohne das Token.

## Widerrufen
- **Bei GitHub:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → das Token →
  **Delete**.
- **In TaskHub:** Einstellungen → Konnektoren → GitHub Actions → **Entfernen** (löscht es aus der Anmeldeinformationsverwaltung).

## Offizielle Dokumentation
- Ein Fine-grained Token erstellen:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Pull Requests suchen (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Berechtigungen pro Endpunkt:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
