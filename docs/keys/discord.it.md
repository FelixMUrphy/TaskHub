# Discord: token del bot per TaskHub

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · [Français](discord.fr.md) · **Italiano** · [Português](discord.pt.md) · [Polski](discord.pl.md) · [中文](discord.zh.md) · [日本語](discord.ja.md) · [Tutte le pagine](README.md)

## A cosa serve a TaskHub
- Invia le notifiche di TaskHub, l'avanzamento delle attività e i grafici come messaggi diretti dal **tuo** bot.
- Riceve la pressione dei tuoi pulsanti e i comandi `/tasks`, `/mute`, `/unmute`, tramite una connessione che il tuo PC apre verso Discord. Non serve nessun indirizzo pubblico.
- Non legge i tuoi messaggi: non chiede intent privilegiati e mai il contenuto dei messaggi.

## Crea il bot
1. Apri il Discord Developer Portal: <https://discord.com/developers/applications?new_application=true>.
2. Clicca **Create App**, inserisci un nome (per esempio `TaskHub`) e clicca **Create**.
3. Nella pagina **Bot**, sotto **Token**, clicca **Reset Token** e copia il token. Discord lo mostra una sola volta.
4. Tieni il bot solo tuo: nella pagina **Installation** imposta **Install Link** su **None**, poi nella pagina **Bot** disattiva **Public Bot**. Con l'opzione disattivata, solo tu puoi aggiungere il bot ai server. (Se il Portal rifiuta, fai prima il passaggio **Installation**.)
5. Lascia il resto com'è: nessun intent privilegiato e **Interactions Endpoint URL** vuoto. Con un URL lì, Discord invia la pressione dei pulsanti a quell'indirizzo invece che a TaskHub, e TaskHub ti avvisa.

## Incollalo in TaskHub
1. TaskHub → **Impostazioni** → **Messaggistica** → **Discord**. Incolla il token e clicca **Connetti**. Un token che Discord rifiuta non viene salvato.
2. Aggiungi il bot a un server (sezione successiva). Discord permette a un bot di scrivere solo a chi condivide con lui un server, quindi fino ad allora TaskHub dice che il bot non può ancora scriverti.
3. Clicca **Controlla di nuovo**. Il bot ti invia un messaggio diretto con un pulsante **Connetti**. Premilo.

Si può collegare solo il proprietario dell'applicazione (per l'applicazione di un team, il proprietario del team). Solo le pressioni di quella persona, in quella chat, hanno effetto.

## Aggiungi il bot a un server
1. In TaskHub, clicca **Aggiungi il bot a un server**. La pagina di Discord si apre nel browser.
2. Nell'elenco dei server scegline uno in cui puoi aggiungere bot: ne sei il proprietario o vi hai **Manage Server**. Il bot non ottiene permessi al suo interno e non lo legge; gli serve solo condividere un server con te.
3. Conferma in quella pagina, poi torna a TaskHub e clicca **Controlla di nuovo**.

**Non hai un server tuo?** Crearne uno è gratis: nell'app di Discord clicca **Add a Server** in fondo alla barra laterale sinistra, scegli **Create My Own**, poi **For me and my friends**, inserisci un nome e clicca **Create**. Poi ripeti i passaggi qui sopra.

**Ancora nessun messaggio?** Le tue impostazioni sulla privacy potrebbero bloccarlo: clicca il nome del server, apri **Privacy Settings**, attiva **Allow direct messages from server members** e clicca **Controlla di nuovo**.

## Dove viene conservato il token
- Solo in Gestione credenziali di Windows, mai in `config.json`. Se Gestione credenziali non è disponibile, TaskHub lo dice e non si connette.
- Viene inviato solo a `discord.com` e al gateway di Discord (`*.discord.gg`).
- Il tuo ID utente Discord, l'ID della chat e l'ID dell'applicazione vengono salvati in `config.json`.

## Revocalo
- **Su Discord:** Developer Portal → la tua applicazione → **Bot** → **Reset Token**. Il vecchio token smette di funzionare.
- **In TaskHub:** Impostazioni → Messaggistica → Discord → **Disconnetti** (lo elimina da Gestione credenziali). Il bot resta nel tuo account Discord finché non elimini l'applicazione. Disinstallare TaskHub con **Elimina anche i miei dati** elimina anche il token; una semplice disinstallazione lo conserva.

## Se il token smette di funzionare
Discord reimposta il token di un bot dopo 1000 nuove connessioni in un giorno e scrive via email al proprietario. TaskHub resta ben al di sotto di questo limite e aspetta quando ci si avvicina. Se succede comunque, la pagina dice che il token è stato rifiutato: crea un token nuovo e connetti di nuovo.

## Documentazione ufficiale
- Per iniziare: <https://docs.discord.com/developers/quick-start/getting-started>
- Bot e messaggi diretti, server in comune: <https://docs.discord.com/developers/interactions/application-commands>
- Creare un server: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Messaggi diretti dai membri del server: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Chi può aggiungere un bot (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL e gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Limiti di connessione: <https://docs.discord.com/developers/events/gateway>
- Informativa sulla privacy: <https://discord.com/privacy>
