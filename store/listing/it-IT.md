# it-IT

## Description

TaskHub raccoglie in un unico posto i lavori lunghi del tuo PC e ti permette di controllarli: esportazioni video, rendering, build, backup, addestramento di modelli e gli script che gli agenti IA di programmazione avviano per te.

Come funziona
Un programma o uno script comunica il proprio avanzamento a TaskHub tramite un'API HTTP locale su 127.0.0.1, il comando taskhub o il client Python incluso. TaskHub mostra:
• un'icona nell'area di notifica con un anello di avanzamento e il numero di attività in corso;
• un pannello con avanzamento, tempo rimanente, stato e registro di ogni attività, più i widget che richiede: grafici a linee, ad area e a barre, indicatori, valori, spie, tabelle;
• i controlli propri dell'attività: pulsanti, campi numerici, di testo o a elenco, pausa, arresto e chiusura forzata. La tua scelta torna all'attività;
• notifiche di Windows quando un'attività termina, fallisce o smette di aggiornarsi, con i pulsanti dell'attività se li richiede.

"taskhub run -- <comando>" avvolge qualsiasi riga di comando: l'output finisce nel registro dell'attività, l'avanzamento viene letto da lì e si aggiungono i pulsanti Arresta e Termina.

Claude Code e Codex
Dal menu dell'area di notifica (Agenti IA) puoi collegare TaskHub a Claude Code e Codex. L'agente ottiene gli strumenti TaskHub (MCP) per segnalare i suoi lavori lunghi, e tu ricevi una notifica quando l'agente finisce una risposta o ha bisogno del tuo permesso. TaskHub aggiunge le sue voci ai file di configurazione degli agenti e le rimuove quando disattivi l'opzione.

Telegram (facoltativo)
Collega il tuo bot Telegram per ricevere le stesse notifiche sul telefono e premere da lì i pulsanti delle attività. I messaggi vanno dal tuo PC direttamente alla Bot API di Telegram.

Privacy
Nessun account, nessun servizio cloud, nessuna telemetria. L'API ascolta solo su 127.0.0.1, e attività e impostazioni restano in %APPDATA%\TaskHub.

Buono a sapersi
• Gli aggiornamenti arrivano dal Microsoft Store.
• L'avvio automatico si disattiva dal menu dell'area di notifica o in Impostazioni → App → Avvio.
• Il riferimento dell'API si apre dal menu dell'area di notifica (Riferimento API).
• Richiede Windows 10 versione 1809 o successiva (x64) e Microsoft Edge WebView2 Runtime, già incluso nelle versioni attuali di Windows.
• Lingue dell'interfaccia: inglese, ucraino, spagnolo, tedesco, francese, italiano, portoghese, polacco, cinese (semplificato), giapponese.

## Product features

- Icona nell'area di notifica con anello di avanzamento e numero di attività in corso
- Pannello con avanzamento, tempo rimanente, registro, grafici, indicatori e tabelle per ogni attività
- Pulsanti e campi dichiarati dall'attività stessa: pausa, arresto, modifica di un parametro, chiusura forzata
- Notifiche di Windows a fine lavoro, in caso di errore o di blocco, con i pulsanti dell'attività
- API HTTP locale, comando taskhub e client Python; "taskhub run" avvolge qualsiasi comando
- Claude Code e Codex: strumenti TaskHub (MCP) e notifiche, attivabili dal menu
- Bot Telegram facoltativo: notifiche, avanzamento e pulsanti dell'attività sul telefono
- Nessun account, nessun cloud, nessuna telemetria; l'API ascolta solo su 127.0.0.1
- Interfaccia in 10 lingue

## Short description

Segui e controlla i lavori lunghi del tuo PC: avanzamento, grafici, notifiche e i pulsanti propri di ogni attività. Funziona con qualsiasi script tramite un'API locale o il comando taskhub, oltre che con Claude Code e Codex.
