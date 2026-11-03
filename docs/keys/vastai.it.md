# Vast.ai: chiave API per TaskHub

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · [Deutsch](vastai.de.md) · [Français](vastai.fr.md) · **Italiano** · [Português](vastai.pt.md) · [Polski](vastai.pl.md) · [中文](vastai.zh.md) · [日本語](vastai.ja.md) · [Tutte le pagine](README.md)

## A cosa serve a TaskHub
- **Osservare:** le tue istanze, il carico, il prezzo, gli addebiti degli ultimi 30 giorni, il saldo e il credito.
- **Azioni** (solo se la chiave le consente): Ferma, Avvia, Riavvia, dal pannello, da una notifica o da Telegram.
- **Elimina (destroy)** non viene mai fatto in automatico. Funziona solo nel pannello di TaskHub, dopo che hai digitato il nome.

## Permessi minimi
| Cosa vuoi | Permessi |
|---|---|
| (a) Solo osservare | `instance_read`, `user_read`, `billing_read` |
| (b) Ferma / Avvia / Riavvia | quelli sopra + `instance_write` |

Attenzione: `instance_write` di Vast.ai copre "creating, managing, and destroying instances and volumes": non esiste
un permesso per fermare / avviare senza poter eliminare. TaskHub non elimina mai da solo, solo dal pannello dopo che hai
digitato il nome.

## Crea la chiave
**Nella console:** apri la sezione **Keys** della console di Vast.ai → **API Keys** → **+New**. Nella finestra
dagli un nome (per esempio `TaskHub`) e scegli i permessi: di default sono selezionati tutti i permessi del tuo account,
quindi lascia solo quelli della tabella. Crea la chiave e copiala.

**Con la CLI:** le pagine dei permessi di Vast.ai descrivono le chiavi limitate tramite CLI / API: usa questo metodo se la
console non ti permette di restringerli. Salva come `perms.json`:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Aggiungi `"instance_write": {}` per le azioni, poi esegui:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## Incollala in TaskHub
1. TaskHub → **Impostazioni** → **Connettori** → **Vast.ai**.
2. Attivalo, incolla la chiave e clicca **Salva**.
3. La riga deve dire **Connesso, saldo …** (oppure **Connesso**). Se Vast.ai rifiuta la chiave, non viene salvato nulla.

TaskHub controlla una volta (e poi ogni giorno) se la chiave può agire e mostra i pulsanti delle azioni solo se può.

## Dove viene conservata la chiave
- Solo in Gestione credenziali di Windows, mai in `config.json`.
- Viene inviata solo a `console.vast.ai`.

## Revocala
- **Su Vast.ai:** console → **Keys** → **Delete** accanto alla chiave (**Reset** la sostituisce con una nuova).
- **In TaskHub:** Impostazioni → Connettori → Vast.ai → **Rimuovi** (la elimina da Gestione credenziali).

## Documentazione ufficiale
- Chiavi API: <https://docs.vast.ai/guides/reference/keys>
- Permessi: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Fatturazione e saldo: <https://docs.vast.ai/guides/reference/billing>
