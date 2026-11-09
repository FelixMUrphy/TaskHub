# Runpod: chiave API per TaskHub

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · [Deutsch](runpod.de.md) · [Français](runpod.fr.md) · **Italiano** · [Português](runpod.pt.md) · [Polski](runpod.pl.md) · [中文](runpod.zh.md) · [日本語](runpod.ja.md) · [Tutte le pagine](README.md)

## A cosa serve a TaskHub
- **Osservare:** i tuoi pod, il carico, il prezzo, quanto hai speso finora (fatturazione Runpod), il saldo e l'autonomia.
- **Azioni** (solo se la chiave le consente): Ferma, Avvia, Riavvia, dal pannello, da una notifica o da Telegram.
- **Elimina** non viene mai fatto in automatico. Funziona solo nel pannello di TaskHub, dopo che hai digitato il nome del pod.

## Crea la chiave
1. Apri la console di Runpod: <https://console.runpod.io/user/credentials>.
2. In **API Keys** clicca **Create API Key**.
3. Inserisci un nome (per esempio `TaskHub`) e scegli il tipo (vedi sotto).
4. Clicca **Create** e copia subito la chiave: viene mostrata una sola volta, Runpod non la conserva.

## Quale tipo scegliere
| Cosa vuoi | Tipo | Note |
|---|---|---|
| (a) Solo osservare | **Read Only** | Consigliato se non ti servono i pulsanti. Runpod non documenta se una chiave Read Only possa leggere la fatturazione e il saldo; se TaskHub non mostra il saldo, il motivo è questo. |
| (b) Ferma / Avvia / Riavvia | **All** | Runpod documenta gli scope **Restricted** solo per gli endpoint Serverless, non per i pod: per le azioni sui pod usa quindi **All**. |

TaskHub controlla una volta (e poi ogni giorno) se la chiave può agire e mostra i pulsanti delle azioni solo se può.
Una chiave di tipo **All** potrebbe anche eliminare i pod: TaskHub non lo fa mai da solo, solo dal pannello dopo che
hai digitato il nome.

## Incollala in TaskHub
1. TaskHub → **Impostazioni** → **Connettori** → **Runpod**.
2. Attivalo, incolla la chiave e clicca **Salva**.
3. La riga deve dire **Connesso, saldo …** (oppure **Connesso**). Se Runpod rifiuta la chiave, non viene salvato nulla.

## Dove viene conservata la chiave
- Solo in Gestione credenziali di Windows, mai in `config.json`.
- Viene inviata solo a `api.runpod.io` (REST per i pod, le dimensioni dei network volume e la fatturazione, GraphQL per il saldo).

## Revocala
- **Su Runpod:** <https://console.runpod.io/user/credentials> → l'icona del cestino accanto alla chiave → **Revoke Key**
  (oppure disattiva la chiave con il suo interruttore).
- **In TaskHub:** Impostazioni → Connettori → Runpod → **Rimuovi** (la elimina da Gestione credenziali).
  Anche disinstallare TaskHub con **Elimina anche i miei dati** la elimina; una semplice disinstallazione la conserva.

## Documentazione ufficiale
- Chiavi API: <https://docs.runpod.io/get-started/credentials>
- Limiti di frequenza: <https://docs.runpod.io/api-reference-v2/rate-limits>
- Prezzi e cosa succede a $0: <https://docs.runpod.io/pods/pricing>
