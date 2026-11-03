# GitHub Actions: token per TaskHub

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · [Deutsch](github.de.md) · [Français](github.fr.md) · **Italiano** · [Português](github.pt.md) · [Polski](github.pl.md) · [中文](github.zh.md) · [日本語](github.ja.md) · [Tutte le pagine](README.md)

## A cosa serve a TaskHub
- **Osservare:** le esecuzioni dei workflow dei repository che elenchi: avanzamento, il passaggio fallito, il risultato.
- **Azioni** (solo con accesso in scrittura): Annulla e Rilancia un'esecuzione.
- **Pull request** (facoltativo, dalla 2.9.4, sola lettura): quelle in attesa della tua revisione e le pull request aperte di Dependabot.
- TaskHub non modifica mai i tuoi workflow, il codice o le impostazioni dei repository.

## Crea un token fine-grained
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (link diretto: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** per esempio `TaskHub`.
3. **Expiration:** 30 giorni di default; scegli quello che ti serve (TaskHub ti avvisa una settimana prima della scadenza).
4. **Resource owner:** tu, oppure l'organizzazione proprietaria dei repository.
5. **Repository access:** **Only select repositories** → i repository che elencherai in TaskHub.
6. **Permissions** (vedi sotto) → **Generate token** → copialo.

Se il proprietario è un'organizzazione che richiede un'approvazione, il token resta in sospeso finché un amministratore non lo approva.

## Permessi minimi
| Cosa vuoi | Permessi del repository |
|---|---|
| (a) Solo osservare | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Annulla / Rilancia | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull request (facoltativo): le pull request di Dependabot richiedono anche **Pull requests: Read-only** (GitHub mostra l'elenco
delle PR aperte solo con questo permesso); la ricerca delle richieste di revisione non richiede alcun permesso. Un token fine-grained vede le richieste
di revisione solo nei repository a cui è stato dato accesso e in quelli pubblici.

Preferisci questo token all'altra opzione di TaskHub (il token che Git Credential Manager ha già), che ha
accesso a tutti i tuoi repository.

## Osservare un altro repository più avanti
Con **Only select repositories** il token raggiunge solo i repository che hai scelto. Se TaskHub dice che un repository è
*non trovato, o il token non ha accesso*: controlla il nome, poi apri il token su GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) e aggiungi il repository in **Repository access**. GitHub risponde allo stesso modo per
un nome sbagliato, per un repository a cui il token non ha accesso e, in alcune richieste, per un permesso mancante, quindi TaskHub non può dirti quale dei tre sia.
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Incollalo in TaskHub
1. TaskHub → **Impostazioni** → **Connettori** → **GitHub Actions**.
2. Attivalo, scegli **Il mio token fine-grained (consigliato)**, incolla il token, elenca i repository
   (`owner/repo`) e clicca **Salva**.
3. La riga deve dire **Connesso come** *il tuo login*. Se GitHub rifiuta il token, non viene salvato nulla.

## Dove viene conservato il token
- Solo in Gestione credenziali di Windows, mai in `config.json`.
- Viene inviato solo a `api.github.com`. La coda del log, facoltativa, viene scaricata tramite il link monouso che dà GitHub,
  senza il token.

## Revocalo
- **Su GitHub:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → il token →
  **Delete**.
- **In TaskHub:** Impostazioni → Connettori → GitHub Actions → **Rimuovi** (lo elimina da Gestione credenziali).

## Documentazione ufficiale
- Creare un token fine-grained:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Cercare le pull request (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Permessi per ogni endpoint:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
