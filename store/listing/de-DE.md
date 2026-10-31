# de-DE

## Description

TaskHub bündelt die lang laufenden Jobs auf Ihrem PC an einer Stelle und lässt Sie sie steuern: Videoexporte, Renderings, Builds, Backups, Modelltraining, CI-Läufe, Cloud-GPU-Maschinen und die Skripte, die KI-Programmieragenten für Sie starten.

So funktioniert es
Ein Programm oder Skript meldet seinen Fortschritt an TaskHub – über eine lokale HTTP-API auf 127.0.0.1, den Befehl taskhub oder den mitgelieferten Python-Client. TaskHub zeigt dann:
• ein Tray-Symbol mit Fortschrittsring und der Zahl aktiver Aufgaben;
• ein Panel mit Fortschritt, Restzeit, Status und Protokoll jeder Aufgabe sowie den Widgets, die sie anfordert: Linien-, Flächen- und Balkendiagramme, Anzeigen, Kennzahlen, Statusanzeigen, Tabellen;
• die eigenen Bedienelemente der Aufgabe: Schaltflächen, Eingaben für Zahl, Text oder Auswahl, Pause, Stopp und Beenden. Ihre Auswahl geht an die Aufgabe zurück;
• Windows-Benachrichtigungen, wenn eine Aufgabe fertig ist, fehlschlägt oder sich nicht mehr meldet – auf Wunsch mit den Schaltflächen der Aufgabe.

„taskhub run -- <Befehl>“ umschließt jede Befehlszeile: Die Ausgabe landet im Aufgabenprotokoll, der Fortschritt wird daraus gelesen, und es gibt Schaltflächen zum Stoppen und Beenden.

Einstellungen im Panel
Alles wird auf einer Seite eingerichtet: Die Schaltfläche ⚙ im Panel (oder „Einstellungen“ im Tray-Menü) öffnet Darstellung, Benachrichtigungen und Telegram, Konnektoren, KI-Agenten, Autostart und die API-Referenz. Im Tray-Menü bleiben die Schnellaktionen: Panel öffnen, stummschalten, Einstellungen, Unterstützen, Beenden.

Claude Code und Codex
Unter Einstellungen → KI-Agenten verbinden Sie TaskHub mit Claude Code und Codex. Der Agent erhält TaskHub-Tools (MCP), um seine langen Jobs zu melden, und Sie bekommen eine Benachrichtigung, wenn der Agent eine Antwort beendet hat oder Ihre Erlaubnis braucht. TaskHub trägt sich in die Einstellungsdateien der Agenten ein und entfernt die Einträge wieder, wenn Sie die Option ausschalten.

Konnektoren (optional, standardmäßig aus)
• GitHub Actions: die Workflow-Läufe der Repositorys, die Sie angeben, als Aufgaben: Fortschritt nach Jobs und Schritten, der fehlgeschlagene Schritt und das Ende seines Protokolls, Schaltflächen zum Abbrechen und erneuten Ausführen.
• Runpod und Vast.ai: Ihre GPU-Maschinen als Aufgaben: GPU, Preis pro Stunde, bisherige Kosten, Laufzeit, SSH, Auslastungsdiagramme, eine Warnung „GPU untätig“, Guthaben und Reichweite mit Warnung bei niedrigem Guthaben, Stoppen / Starten / Neustarten (Löschen nur im Panel, nach Eingabe des Maschinennamens).
Jeder Konnektor arbeitet erst, wenn Sie ihn einschalten und Ihren eigenen API-Schlüssel eingeben. Der Schlüssel liegt nur in der Windows-Anmeldeinformationsverwaltung, und der Konnektor spricht nur mit der API seines eigenen Dienstes (api.github.com, api.runpod.io, console.vast.ai).

Guthabenschutz (optional, aus, bis Sie ihn einschalten)
Für Runpod und Vast.ai: eine Warnung an Ihrer eigenen Guthabenschwelle, und an der Stopp-Schwelle werden die laufenden Maschinen nach einer Benachrichtigung mit „Jetzt stoppen“ und „Verschieben“ gestoppt. Er stoppt Maschinen nur und löscht sie nie; kann das Guthaben nicht gelesen werden, stoppt er nichts.

Telegram (optional)
Verbinden Sie Ihren eigenen Telegram-Bot, um dieselben Benachrichtigungen und eine laufend aktualisierte Fortschrittsnachricht pro Aufgabe auf dem Handy zu erhalten und dort die Schaltflächen der Aufgaben zu drücken. Das Bot-Token liegt in der Windows-Anmeldeinformationsverwaltung, und die Nachrichten gehen direkt von Ihrem PC an die Bot API von Telegram.

Datenschutz
Kein Konto, keine Telemetrie. Die API lauscht nur auf 127.0.0.1; Aufgaben und Einstellungen bleiben in %APPDATA%\TaskHub. Nichts verlässt Ihren PC, solange Sie nicht Telegram oder einen Konnektor einschalten.

Gut zu wissen
• Updates kommen über den Microsoft Store.
• Der Autostart lässt sich unter Einstellungen → System oder in den Windows-Einstellungen → Apps → Autostart ausschalten.
• Die API-Referenz öffnen Sie auf der Einstellungsseite.
• Voraussetzungen: Windows 10 Version 2004 oder neuer (x64) und die Microsoft Edge WebView2 Runtime, die aktuelles Windows bereits enthält.
• Sprachen der Oberfläche: Englisch, Ukrainisch, Spanisch, Deutsch, Französisch, Italienisch, Portugiesisch, Polnisch, Chinesisch (vereinfacht), Japanisch.

## What's new in this version

TaskHub 2.9
• Einstellungsseite im Panel (⚙): alles an einer Stelle; das Tray-Menü behält die Schnellaktionen im Stil von Windows 11.
• Neue Konnektoren, standardmäßig aus: GitHub Actions (Workflow-Läufe mit Abbrechen und erneutem Ausführen), Runpod und Vast.ai (GPU-Maschinen, Kosten, Guthaben, Warnung bei untätiger GPU, Stoppen und Starten). Ihre API-Schlüssel liegen nur in der Windows-Anmeldeinformationsverwaltung.
• Guthabenschutz für Runpod und Vast.ai, aus, bis Sie ihn einschalten: eine Warnung an Ihrer Schwelle, und an der Stopp-Schwelle werden laufende Maschinen gestoppt, nie gelöscht.
• Telegram: Das Bot-Token zieht in die Windows-Anmeldeinformationsverwaltung um; TaskHub hält sich an das Nachrichtentempo von Telegram, statt Nachrichten zu verlieren.
• Einstellung für die Transparenz der Karten; Links auf Dateien und Ordner werden im Explorer gezeigt, nie ausgeführt.
• 2.9.1: Geldbeträge mit zwei Nachkommastellen, Speicherkosten der Runpod-Network-Volumes.
• Erfordert jetzt Windows 10 Version 2004 oder neuer.

## Product features

- Tray-Symbol mit Fortschrittsring und Zahl der aktiven Aufgaben
- Panel mit Fortschritt, Restzeit, Protokoll, Diagrammen, Anzeigen und Tabellen für jede Aufgabe
- Schaltflächen und Eingaben, die die Aufgabe selbst festlegt: Pause, Stopp, Parameter ändern, Beenden
- Windows-Benachrichtigungen bei Abschluss, Fehler und Stillstand, mit den Schaltflächen der Aufgabe
- Lokale HTTP-API, Befehl taskhub und Python-Client; „taskhub run“ umschließt jeden Befehl
- Alle Einstellungen auf einer Seite im Panel (⚙); Schnellaktionen im Tray-Menü
- Claude Code und Codex: TaskHub-Tools (MCP) und Benachrichtigungen, in den Einstellungen einschaltbar
- Optionaler Konnektor für GitHub Actions: Workflow-Läufe als Aufgaben, mit Abbrechen und erneutem Ausführen
- Optionale Konnektoren für Runpod und Vast.ai: GPU-Maschinen, Kosten, Guthaben, Warnung bei untätiger GPU, Stoppen und Starten
- Optionaler Guthabenschutz: warnt bei niedrigem Guthaben und stoppt (löscht nie) die laufenden Maschinen
- Optionaler Telegram-Bot: Benachrichtigungen, Fortschritt und Schaltflächen der Aufgabe auf dem Handy
- Kein Konto, keine Telemetrie; API-Schlüssel nur in der Windows-Anmeldeinformationsverwaltung; die API lauscht nur auf 127.0.0.1
- 10 Sprachen der Oberfläche

## Short description

Lange Jobs auf dem PC im Blick behalten und steuern: Fortschritt, Diagramme, Benachrichtigungen und eigene Schaltflächen jeder Aufgabe. Für jedes Skript per lokaler API oder Befehl taskhub, für Claude Code und Codex, optional für GitHub Actions, Runpod und Vast.ai.
