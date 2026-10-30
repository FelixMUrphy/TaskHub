# de-DE

## Description

TaskHub bündelt die lang laufenden Jobs auf Ihrem PC an einer Stelle und lässt Sie sie steuern: Videoexporte, Renderings, Builds, Backups, Modelltraining und die Skripte, die KI-Programmieragenten für Sie starten.

So funktioniert es
Ein Programm oder Skript meldet seinen Fortschritt an TaskHub – über eine lokale HTTP-API auf 127.0.0.1, den Befehl taskhub oder den mitgelieferten Python-Client. TaskHub zeigt dann:
• ein Tray-Symbol mit Fortschrittsring und der Zahl aktiver Aufgaben;
• ein Panel mit Fortschritt, Restzeit, Status und Protokoll jeder Aufgabe sowie den Widgets, die sie anfordert: Linien-, Flächen- und Balkendiagramme, Anzeigen, Kennzahlen, Statusanzeigen, Tabellen;
• die eigenen Bedienelemente der Aufgabe: Schaltflächen, Eingaben für Zahl, Text oder Auswahl, Pause, Stopp und Beenden. Ihre Auswahl geht an die Aufgabe zurück;
• Windows-Benachrichtigungen, wenn eine Aufgabe fertig ist, fehlschlägt oder sich nicht mehr meldet – auf Wunsch mit den Schaltflächen der Aufgabe.

„taskhub run -- <Befehl>“ umschließt jede Befehlszeile: Die Ausgabe landet im Aufgabenprotokoll, der Fortschritt wird daraus gelesen, und es gibt Schaltflächen zum Stoppen und Beenden.

Claude Code und Codex
Im Tray-Menü (KI-Agenten) verbinden Sie TaskHub mit Claude Code und Codex. Der Agent erhält TaskHub-Tools (MCP), um seine langen Jobs zu melden, und Sie bekommen eine Benachrichtigung, wenn der Agent eine Antwort beendet hat oder Ihre Erlaubnis braucht. TaskHub trägt sich in die Einstellungsdateien der Agenten ein und entfernt die Einträge wieder, wenn Sie die Option ausschalten.

Telegram (optional)
Verbinden Sie Ihren eigenen Telegram-Bot, um dieselben Benachrichtigungen auf dem Handy zu erhalten und dort die Schaltflächen der Aufgaben zu drücken. Die Nachrichten gehen direkt von Ihrem PC an die Bot API von Telegram.

Datenschutz
Kein Konto, kein Cloud-Dienst, keine Telemetrie. Die API lauscht nur auf 127.0.0.1; Aufgaben und Einstellungen bleiben in %APPDATA%\TaskHub.

Gut zu wissen
• Updates kommen über den Microsoft Store.
• Der Autostart lässt sich im Tray-Menü oder unter Einstellungen → Apps → Autostart ausschalten.
• Die API-Referenz öffnen Sie über das Tray-Menü (API-Referenz).
• Voraussetzungen: Windows 10 Version 1809 oder neuer (x64) und die Microsoft Edge WebView2 Runtime, die aktuelles Windows bereits enthält.
• Sprachen der Oberfläche: Englisch, Ukrainisch, Spanisch, Deutsch, Französisch, Italienisch, Portugiesisch, Polnisch, Chinesisch (vereinfacht), Japanisch.

## Product features

- Tray-Symbol mit Fortschrittsring und Zahl der aktiven Aufgaben
- Panel mit Fortschritt, Restzeit, Protokoll, Diagrammen, Anzeigen und Tabellen für jede Aufgabe
- Schaltflächen und Eingaben, die die Aufgabe selbst festlegt: Pause, Stopp, Parameter ändern, Beenden
- Windows-Benachrichtigungen bei Abschluss, Fehler und Stillstand, mit den Schaltflächen der Aufgabe
- Lokale HTTP-API, Befehl taskhub und Python-Client; „taskhub run“ umschließt jeden Befehl
- Claude Code und Codex: TaskHub-Tools (MCP) und Benachrichtigungen, im Tray-Menü einschaltbar
- Optionaler Telegram-Bot: Benachrichtigungen, Fortschritt und Schaltflächen der Aufgabe auf dem Handy
- Kein Konto, keine Cloud, keine Telemetrie; die API lauscht nur auf 127.0.0.1
- 10 Sprachen der Oberfläche

## Short description

Lang laufende Jobs auf dem PC im Blick behalten und steuern: Fortschritt, Diagramme, Benachrichtigungen und eigene Schaltflächen jeder Aufgabe. Funktioniert mit jedem Skript über eine lokale API oder den Befehl taskhub sowie mit Claude Code und Codex.
