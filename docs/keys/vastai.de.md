# Vast.ai: API-Schlüssel für TaskHub

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · **Deutsch** · [Français](vastai.fr.md) · [Italiano](vastai.it.md) · [Português](vastai.pt.md) · [Polski](vastai.pl.md) · [中文](vastai.zh.md) · [日本語](vastai.ja.md) · [Alle Seiten](README.md)

## Wofür TaskHub ihn braucht
- **Beobachten:** deine Instanzen, ihre Auslastung, den Preis, die Kosten der letzten 30 Tage, dein Guthaben und deinen Kredit.
- **Aktionen** (nur wenn der Schlüssel sie erlaubt): Stoppen, Starten, Neu booten – über das Panel, eine Benachrichtigung oder Telegram.
- **Löschen (Destroy)** passiert nie automatisch. Es geht nur im TaskHub-Panel, nachdem du den Namen eingegeben hast.

## Minimale Berechtigungen
| Du willst | Berechtigungen |
|---|---|
| (a) Nur beobachten | `instance_read`, `user_read`, `billing_read` |
| (b) Stoppen / Starten / Neu booten | die obigen + `instance_write` |

Hinweis: `instance_write` bei Vast.ai umfasst „creating, managing, and destroying instances and volumes“ – es gibt
keine Berechtigung für Stoppen / Starten ohne Destroy. TaskHub zerstört nie von selbst, nur über das Panel, nachdem du
den Namen eingegeben hast.

## Schlüssel erstellen
**In der Konsole:** Öffne den Bereich **Keys** der Vast.ai-Konsole → **API Keys** → **+New**. Gib im Pop-up
einen Namen ein (z. B. `TaskHub`) und wähle die Berechtigungen – standardmäßig sind alle Berechtigungen deines Kontos ausgewählt,
lass also nur die aus der Tabelle stehen. Erstelle ihn und kopiere den Schlüssel.

**Mit der CLI:** Die Berechtigungsseiten von Vast.ai beschreiben eingeschränkte Schlüssel über die CLI / API – nutze das, wenn
die Konsole keine Einschränkung erlaubt. Speichere als `perms.json`:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Ergänze `"instance_write": {}` für Aktionen und führe dann aus:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## In TaskHub einfügen
1. TaskHub → **Einstellungen** → **Konnektoren** → **Vast.ai**.
2. Schalte ihn ein, füge den Schlüssel ein und klicke auf **Speichern**.
3. Die Zeile sollte **Verbunden, Guthaben …** (oder **Verbunden**) lauten. Wenn Vast.ai den Schlüssel ablehnt, wird nichts gespeichert.

TaskHub prüft einmal (und danach täglich), ob der Schlüssel handeln darf, und zeigt die Aktionsschaltflächen nur dann an.

## Wo der Schlüssel gespeichert wird
- Nur in der Windows-Anmeldeinformationsverwaltung – nie in `config.json`.
- Er wird nur an `console.vast.ai` gesendet.

## Widerrufen
- **Bei Vast.ai:** Konsole → **Keys** → **Delete** neben dem Schlüssel (**Reset** ersetzt ihn durch einen neuen).
- **In TaskHub:** Einstellungen → Konnektoren → Vast.ai → **Entfernen** (löscht ihn aus der Anmeldeinformationsverwaltung).

## Offizielle Dokumentation
- API-Schlüssel: <https://docs.vast.ai/guides/reference/keys>
- Berechtigungen: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Abrechnung und Guthaben: <https://docs.vast.ai/guides/reference/billing>
