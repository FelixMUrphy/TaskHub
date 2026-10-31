# it-IT

## Description

TaskHub raccoglie in un unico posto i lavori lunghi del tuo PC e ti permette di controllarli: esportazioni video, rendering, build, backup, addestramento di modelli, esecuzioni di CI, macchine GPU nel cloud e gli script che gli agenti IA di programmazione avviano per te.

Come funziona
Un programma o uno script comunica il proprio avanzamento a TaskHub tramite un'API HTTP locale su 127.0.0.1, il comando taskhub o il client Python incluso. TaskHub mostra:
• un'icona nell'area di notifica con un anello di avanzamento e il numero di attività in corso;
• un pannello con avanzamento, tempo rimanente, stato e registro di ogni attività, più i widget che richiede: grafici a linee, ad area e a barre, indicatori, valori, spie, tabelle;
• i controlli propri dell'attività: pulsanti, campi numerici, di testo o a elenco, pausa, arresto e chiusura forzata. La tua scelta torna all'attività;
• notifiche di Windows quando un'attività termina, fallisce o smette di aggiornarsi, con i pulsanti dell'attività se li richiede.

"taskhub run -- <comando>" avvolge qualsiasi riga di comando: l'output finisce nel registro dell'attività, l'avanzamento viene letto da lì e si aggiungono i pulsanti Arresta e Termina.

Impostazioni nel pannello
Tutto si configura in un'unica pagina: il pulsante ⚙ del pannello (o Impostazioni nel menu dell'area di notifica) apre aspetto, notifiche e Telegram, connettori, agenti IA, avvio automatico e riferimento dell'API. Il menu dell'area di notifica mantiene le azioni rapide: apri il pannello, silenzia, impostazioni, sostieni, esci.

Claude Code e Codex
In Impostazioni → Agenti IA puoi collegare TaskHub a Claude Code e Codex. L'agente ottiene gli strumenti TaskHub (MCP) per segnalare i suoi lavori lunghi, e tu ricevi una notifica quando l'agente finisce una risposta o ha bisogno del tuo permesso. TaskHub aggiunge le sue voci ai file di configurazione degli agenti e le rimuove quando disattivi l'opzione.

Connettori (facoltativi, disattivati per impostazione predefinita)
• GitHub Actions: le esecuzioni dei workflow dei repository che indichi, come attività: avanzamento per job e passaggi, il passaggio non riuscito e la fine del suo registro, pulsanti per annullare e rieseguire.
• Runpod e Vast.ai: le tue macchine GPU come attività: GPU, prezzo orario, spesa finora, tempo di attività, SSH, grafici di carico, avviso "GPU inattiva", saldo e autonomia con avviso di saldo basso, arresto / avvio / riavvio (eliminazione solo nel pannello, dopo aver digitato il nome della macchina).
Ogni connettore funziona solo dopo che lo attivi e inserisci la tua chiave API. La chiave è conservata solo in Gestione credenziali di Windows, e il connettore comunica solo con l'API del proprio servizio (api.github.com, api.runpod.io, console.vast.ai).

Protezione del saldo (facoltativa, disattivata finché non la attivi)
Per Runpod e Vast.ai: un avviso alla tua soglia di saldo e, alla soglia di arresto, le macchine attive vengono fermate dopo una notifica con Ferma ora e Rimanda. Ferma soltanto le macchine e non le elimina mai; se il saldo non è leggibile, non ferma nulla.

Telegram (facoltativo)
Collega il tuo bot Telegram per ricevere sul telefono le stesse notifiche e un messaggio di avanzamento in tempo reale per ogni attività, e premere da lì i pulsanti delle attività. Il token del bot è conservato in Gestione credenziali di Windows, e i messaggi vanno dal tuo PC direttamente alla Bot API di Telegram.

Privacy
Nessun account, nessuna telemetria. L'API ascolta solo su 127.0.0.1, e attività e impostazioni restano in %APPDATA%\TaskHub. Nulla lascia il tuo PC finché non attivi Telegram o un connettore.

Buono a sapersi
• Gli aggiornamenti arrivano dal Microsoft Store.
• L'avvio automatico si disattiva in Impostazioni → Sistema o in Impostazioni di Windows → App → Avvio.
• Il riferimento dell'API si apre dalla pagina delle impostazioni.
• Richiede Windows 10 versione 2004 o successiva (x64) e Microsoft Edge WebView2 Runtime, già incluso nelle versioni attuali di Windows.
• Lingue dell'interfaccia: inglese, ucraino, spagnolo, tedesco, francese, italiano, portoghese, polacco, cinese (semplificato), giapponese.

## What's new in this version

TaskHub 2.9
• Pagina delle impostazioni nel pannello (⚙): tutto in un unico posto; il menu dell'area di notifica mantiene le azioni rapide, nello stile di Windows 11.
• Nuovi connettori, disattivati per impostazione predefinita: GitHub Actions (esecuzioni dei workflow con annulla e riesegui), Runpod e Vast.ai (macchine GPU, spesa, saldo, avviso GPU inattiva, arresto e avvio). Le tue chiavi API sono conservate solo in Gestione credenziali di Windows.
• Protezione del saldo per Runpod e Vast.ai, disattivata finché non la attivi: un avviso alla tua soglia e, alla soglia di arresto, le macchine attive vengono fermate, mai eliminate.
• Telegram: il token del bot passa in Gestione credenziali di Windows; TaskHub rispetta il ritmo dei messaggi di Telegram invece di perderli.
• Impostazione della trasparenza delle schede; i collegamenti a file e cartelle vengono mostrati in Esplora file, mai eseguiti.
• 2.9.1: importi con due decimali, costo di archiviazione dei network volume di Runpod.
• Ora richiede Windows 10 versione 2004 o successiva.

## Product features

- Icona nell'area di notifica con anello di avanzamento e numero di attività in corso
- Pannello con avanzamento, tempo rimanente, registro, grafici, indicatori e tabelle per ogni attività
- Pulsanti e campi dichiarati dall'attività stessa: pausa, arresto, modifica di un parametro, chiusura forzata
- Notifiche di Windows a fine lavoro, in caso di errore o di blocco, con i pulsanti dell'attività
- API HTTP locale, comando taskhub e client Python; "taskhub run" avvolge qualsiasi comando
- Tutte le impostazioni in una pagina del pannello (⚙); azioni rapide nel menu dell'area di notifica
- Claude Code e Codex: strumenti TaskHub (MCP) e notifiche, attivabili nelle impostazioni
- Connettore GitHub Actions facoltativo: esecuzioni dei workflow come attività, con annulla e riesegui
- Connettori Runpod e Vast.ai facoltativi: macchine GPU, spesa, saldo, avviso GPU inattiva, arresto e avvio
- Protezione del saldo facoltativa: avvisa con saldo basso e ferma (non elimina mai) le macchine attive
- Bot Telegram facoltativo: notifiche, avanzamento e pulsanti dell'attività sul telefono
- Nessun account, nessuna telemetria; chiavi API solo in Gestione credenziali di Windows; l'API ascolta solo su 127.0.0.1
- Interfaccia in 10 lingue

## Short description

Segui e controlla i lavori lunghi del tuo PC: avanzamento, grafici, notifiche e i pulsanti propri di ogni attività. Funziona con qualsiasi script tramite un'API locale o il comando taskhub, con Claude Code e Codex e, se vuoi, con GitHub Actions, Runpod e Vast.ai.
