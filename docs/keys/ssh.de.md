# SSH: einen Host für TaskHub einrichten

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · **Deutsch** · [Français](ssh.fr.md) · [Italiano](ssh.it.md) · [Português](ssh.pt.md) · [Polski](ssh.pl.md) · [中文](ssh.zh.md) · [日本語](ssh.ja.md) · [Alle Seiten](README.md)

## Was TaskHub damit macht
- TaskHub startet das eigene `ssh.exe` von Windows und liest jeden Host, den du angibst: Last, Speicher, Laufwerke, GPU, tmux-Sitzungen und einen Prozess, den du benennst.
- **TaskHub speichert weder Schlüssel noch Passwort.** Es nutzt deine `~/.ssh/config`, deinen SSH-Agent und deine `known_hosts` und liest sie nur.
- Es installiert nichts auf dem Host und ändert dort nichts. Vorerst funktioniert es mit Linux-Hosts.

## Bevor du einen Host hinzufügst
TaskHub kann keine Fragen beantworten, deshalb müssen drei Dinge ohne Nachfrage funktionieren.

1. **Der OpenSSH-Client.** TaskHub nutzt den in Windows, keine Kopie von Git. In einem Terminal sollte `C:\Windows\System32\OpenSSH\ssh.exe -V` eine Version ausgeben. Falls nicht: Windows-Einstellungen → System → Optionale Features (unter Windows 10: Apps → Optionale Features) → *OpenSSH-Client* hinzufügen ([Schritte von Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **Ein Schlüssel ohne Eintippen.** Ein Schlüssel mit Passphrase muss im Agent sein. Einmalig in PowerShell als Administrator:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Danach als du selbst: `ssh-add`. ([Microsofts Anleitung zu Schlüsseln](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Agents von 1Password oder Pageant funktionieren, wenn sie das OpenSSH von Windows bedienen.
3. **Der Schlüssel des Hosts ist vertraut.** Verbinde dich einmal im Terminal: `ssh my-server`. Vergleiche den angezeigten Fingerabdruck mit dem, den du vom Besitzer des Hosts bekommst, und antworte `yes`. TaskHub vertraut einem neuen Schlüssel nie von selbst.

Die Probe: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` gibt ohne Fragen `Linux` aus.

Der Name eines Hosts steht in `~/.ssh/config`, zusammen mit Benutzer, Port und Schlüssel:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## Hinzufügen
Einstellungen → *Konnektoren* → *SSH* → einschalten, `my-server` eingeben und auf *Hinzufügen* klicken. Nur Buchstaben, Ziffern, Punkte, Bindestriche und Unterstriche: `user@host` gehört in die Konfiguration.

## Was du siehst
Eine Aufgabe pro Host, mit seiner Last und seinen Laufwerken. Du bekommst eine Benachrichtigung, wenn ein Laufwerk voll wird, wenn die Verbindung verloren geht oder TaskHub das Verbinden aufgeben muss, wenn der Host neu startet und wenn ein Job endet:
- **eine tmux-Sitzung** oder ein Befehl darin, der zur Shell zurückkehrt (auch das Schließen eines Editors zählt; ein Schalter in den Einstellungen schaltet diese Meldungen ab, sie bleiben im Log der Aufgabe);
- **ein Prozess, den du benennst:** *Prozess beobachten* bei der Aufgabe des Hosts, mit seiner Nummer (zum Beispiel `pgrep -f train`).

TaskHub kann nicht erkennen, wie ein Job geendet hat, und sagt deshalb „beendet“, nicht „erfolgreich“. Solange dein PC aus ist, schaut niemand zu. Ein Prozess, den du benannt hast und der inzwischen beendet wurde, wird bei der nächsten Verbindung gemeldet. Änderungen an tmux und ein Neustart des Hosts werden nur über eine verlorene Verbindung hinweg bemerkt, während TaskHub läuft: nicht nach einem Neustart von TaskHub oder deinem PC.

## Gut zu wissen
- **Deine Konfiguration wird ausgeführt.** Ein `ProxyCommand` oder `Match exec` in `~/.ssh/config` läuft bei jeder Verbindung, die TaskHub herstellt, genau wie bei dir im Terminal. Hosts hinter `ProxyJump` nutzen die Einstellungen deiner Konfiguration für den Jump-Host.
- **Was offen bleibt.** Ein `ssh` pro Host, solange der Konnektor eingeschaltet ist. Es sendet jede Minute ein festes, nur lesendes Skript (standardmäßig; höchstens alle 30 Sekunden).
- **Ein geänderter Schlüssel stoppt es.** Wenn sich der Schlüssel eines Hosts ändert, verbindet sich TaskHub nicht und sagt es dir. Es kann ein neu installierter Host sein oder jemand dazwischen: Prüfe es und korrigiere dann `known_hosts` selbst.
- **Fehlgeschlagene Anmeldungen.** Nach einem abgelehnten Schlüssel hört TaskHub auf, es zu versuchen, bis du auf *Erneut prüfen* klickst, damit deine Adresse nicht gesperrt wird.
- **Was deinen PC verlässt.** Nur an deine Hosts: dein Benutzername und öffentliche Schlüssel (wie bei jedem ssh) sowie das Skript. Was zurückkommt, bleibt im Datenordner von TaskHub und erscheint in Benachrichtigungen, auch in Telegram oder Discord, wenn diese eingeschaltet sind. Siehe [PRIVACY.md](../../PRIVACY.md).

## Beenden
Schalte den Konnektor aus oder entferne den Host: Die Verbindung wird beendet. Auf dem Host bleibt nichts zurück, und in TaskHub gibt es keinen Schlüssel zu löschen.
