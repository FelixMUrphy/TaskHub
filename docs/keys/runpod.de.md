# Runpod: API-Schlüssel für TaskHub

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · **Deutsch** · [Français](runpod.fr.md) · [Italiano](runpod.it.md) · [Português](runpod.pt.md) · [Polski](runpod.pl.md) · [中文](runpod.zh.md) · [日本語](runpod.ja.md) · [Alle Seiten](README.md)

## Wofür TaskHub ihn braucht
- **Beobachten:** deine Pods, ihre Auslastung, den Preis, die bisherigen Kosten (Runpod-Abrechnung), dein Guthaben und die Restlaufzeit.
- **Aktionen** (nur wenn der Schlüssel sie erlaubt): Stoppen, Starten, Neu starten – über das Panel, eine Benachrichtigung oder Telegram.
- **Löschen** passiert nie automatisch. Es geht nur im TaskHub-Panel, nachdem du den Namen des Pods eingegeben hast.

## Schlüssel erstellen
1. Öffne die Runpod-Konsole: <https://console.runpod.io/user/credentials>.
2. Klicke unter **API Keys** auf **Create API Key**.
3. Gib einen Namen ein (z. B. `TaskHub`) und wähle den Typ (siehe unten).
4. Klicke auf **Create** und kopiere den Schlüssel sofort – er wird nur einmal angezeigt; Runpod speichert ihn nicht.

## Welchen Typ wählen
| Du willst | Typ | Hinweise |
|---|---|---|
| (a) Nur beobachten | **Read Only** | Empfohlen, wenn du die Schaltflächen nicht brauchst. Runpod dokumentiert nicht, ob ein Read-Only-Schlüssel die Abrechnung und das Guthaben lesen darf; wenn TaskHub kein Guthaben zeigt, liegt es daran. |
| (b) Stoppen / Starten / Neu starten | **All** | Runpod dokumentiert **Restricted**-Bereiche nur für Serverless-Endpunkte, nicht für Pods – für Pod-Aktionen nimm also **All**. |

TaskHub prüft einmal (und danach täglich), ob der Schlüssel handeln darf, und zeigt die Aktionsschaltflächen nur dann an.
Ein Schlüssel vom Typ **All** könnte auch Pods löschen – TaskHub tut das nie von selbst, nur über das Panel, nachdem
du den Namen eingegeben hast.

## In TaskHub einfügen
1. TaskHub → **Einstellungen** → **Konnektoren** → **Runpod**.
2. Schalte ihn ein, füge den Schlüssel ein und klicke auf **Speichern**.
3. Die Zeile sollte **Verbunden, Guthaben …** (oder **Verbunden**) lauten. Wenn Runpod den Schlüssel ablehnt, wird nichts gespeichert.

## Wo der Schlüssel gespeichert wird
- Nur in der Windows-Anmeldeinformationsverwaltung – nie in `config.json`.
- Er wird nur an `api.runpod.io` gesendet (REST für Pods, Größen von Network Volumes und Abrechnung, GraphQL für das Guthaben).

## Widerrufen
- **Bei Runpod:** <https://console.runpod.io/user/credentials> → das Papierkorb-Symbol neben dem Schlüssel → **Revoke Key**
  (oder den Schlüssel mit seinem Schalter ausschalten).
- **In TaskHub:** Einstellungen → Konnektoren → Runpod → **Entfernen** (löscht ihn aus der Anmeldeinformationsverwaltung).
  Auch die Deinstallation von TaskHub mit **Auch meine Daten löschen** löscht ihn; eine einfache Deinstallation behält ihn.

## Offizielle Dokumentation
- API-Schlüssel: <https://docs.runpod.io/get-started/credentials>
- Rate Limits: <https://docs.runpod.io/api-reference-v2/rate-limits>
- Preise und was bei 0 $ passiert: <https://docs.runpod.io/pods/pricing>
