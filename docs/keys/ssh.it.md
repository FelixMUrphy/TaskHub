# SSH: configurare un host per TaskHub

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · [Deutsch](ssh.de.md) · [Français](ssh.fr.md) · **Italiano** · [Português](ssh.pt.md) · [Polski](ssh.pl.md) · [中文](ssh.zh.md) · [日本語](ssh.ja.md) · [Tutte le pagine](README.md)

## Cosa fa TaskHub con SSH
- TaskHub esegue il `ssh.exe` di Windows e legge ogni host che elenchi: carico, memoria, dischi, GPU, sessioni tmux e un processo che indichi tu.
- **TaskHub non conserva né chiavi né password.** Usa il tuo `~/.ssh/config`, il tuo agente SSH e il tuo `known_hosts`, e li legge soltanto.
- Non installa nulla sull'host e non vi cambia nulla. Per ora funziona con host Linux.

## Prima di aggiungere un host
TaskHub non può rispondere a domande, quindi tre cose devono funzionare senza.

1. **Il client OpenSSH.** TaskHub usa quello di Windows, non una copia di Git. In un terminale, `C:\Windows\System32\OpenSSH\ssh.exe -V` deve stampare una versione. Se non lo fa: Impostazioni di Windows → Sistema → Funzionalità facoltative (su Windows 10: App → Funzionalità facoltative) → aggiungi *Client OpenSSH* ([i passaggi di Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **Una chiave senza digitare nulla.** Una chiave con passphrase deve essere nell'agente. In PowerShell come amministratore, una volta sola:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Poi, con il tuo utente: `ssh-add`. ([La guida di Microsoft alle chiavi](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Gli agenti di 1Password o Pageant funzionano se servono l'OpenSSH di Windows.
3. **La chiave dell'host è considerata attendibile.** Connettiti una volta da un terminale: `ssh my-server`. Confronta l'impronta che mostra con quella del proprietario dell'host e rispondi `yes`. TaskHub non si fida mai da solo di una chiave nuova.

La verifica: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` stampa `Linux` senza fare domande.

Il nome di un host va in `~/.ssh/config`, insieme a utente, porta e chiave:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## Aggiungilo
Impostazioni → *Connettori* → *SSH* → attivalo, scrivi `my-server` e premi *Aggiungi*. Solo lettere, cifre, punti, trattini e trattini bassi: `user@host` va nella config.

## Cosa vedi
Un'attività per ogni host, con il suo carico e i suoi dischi. Ricevi una notifica quando un disco si riempie, quando la connessione si perde o TaskHub deve smettere di riprovare, quando l'host si riavvia e quando un job termina:
- **una sessione tmux**, oppure un comando al suo interno che torna alla shell (conta anche chiudere un editor; un interruttore nelle Impostazioni le disattiva, e restano nel log dell'attività);
- **un processo che indichi tu:** *Osserva un processo* nell'attività dell'host, con il suo numero (per esempio `pgrep -f train`).

TaskHub non può sapere come è finito un job, quindi dice "terminato", non "riuscito". Quando il tuo PC è spento nessuno osserva. Un processo che hai indicato e che è terminato nel frattempo viene segnalato alla connessione successiva. Le modifiche a tmux e il riavvio dell'host vengono notati solo durante una connessione persa, mentre TaskHub resta in esecuzione: non dopo il riavvio di TaskHub o del tuo PC.

## Cosa sapere
- **La tua config viene eseguita.** Un `ProxyCommand` o un `Match exec` in `~/.ssh/config` viene eseguito a ogni connessione che fa TaskHub, come farebbe per te in un terminale. Gli host dietro `ProxyJump` usano le impostazioni della tua config per l'host di salto.
- **Cosa resta aperto.** Un `ssh` per host finché il connettore è attivo. Invia ogni minuto uno script fisso di sola lettura (al minimo ogni 30 secondi).
- **Una chiave cambiata lo ferma.** Se la chiave di un host cambia, TaskHub non si connette e te lo dice. Può essere un host reinstallato o qualcuno nel mezzo: controlla, poi correggi `known_hosts` da solo.
- **Accessi falliti.** Dopo una chiave rifiutata TaskHub smette di riprovare finché non premi *Controlla di nuovo*, così non può far bannare il tuo indirizzo.
- **Cosa lascia il tuo PC.** Solo verso i tuoi host: il tuo nome utente e le chiavi pubbliche (come fa qualunque ssh), e lo script. Ciò che torna indietro resta nella cartella dati di TaskHub e compare nelle notifiche, anche su Telegram o Discord se attivi. Vedi [PRIVACY.md](../../PRIVACY.md).

## Ferma
Disattiva il connettore o rimuovi l'host: la connessione termina. Sull'host non resta nulla, e in TaskHub non c'è nessuna chiave da eliminare.
