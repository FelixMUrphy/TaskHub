# Discord: Bot-Token für TaskHub

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · **Deutsch** · [Français](discord.fr.md) · [Italiano](discord.it.md) · [Português](discord.pt.md) · [Polski](discord.pl.md) · [中文](discord.zh.md) · [日本語](discord.ja.md) · [Alle Seiten](README.md)

## Wofür TaskHub es braucht
- Sendet deine TaskHub-Benachrichtigungen, den Fortschritt von Aufgaben und Diagramme als Direktnachrichten von **deinem eigenen** Bot.
- Empfängt deine Tastendrücke auf Schaltflächen und die Befehle `/tasks`, `/mute`, `/unmute` über eine Verbindung, die dein PC zu Discord öffnet. Eine öffentliche Adresse ist nicht nötig.
- Es liest deine Nachrichten nicht: Es fragt keine privilegierten Intents an und nie den Nachrichteninhalt.

## Den Bot erstellen
1. Öffne das Discord Developer Portal: <https://discord.com/developers/applications?new_application=true>.
2. Klicke auf **Create App**, gib einen Namen ein (z. B. `TaskHub`) und klicke auf **Create**.
3. Klicke auf der Seite **Bot** unter **Token** auf **Reset Token** und kopiere das Token. Discord zeigt es nur einmal an.
4. Halte den Bot für dich allein: Stelle auf der Seite **Installation** **Install Link** auf **None**, schalte dann auf der Seite **Bot** **Public Bot** aus. Ist es aus, kann nur du den Bot zu Servern hinzufügen. (Wenn das Portal das ablehnt, mach zuerst den Schritt auf **Installation**.)
5. Lass den Rest, wie er ist: keine privilegierten Intents und **Interactions Endpoint URL** leer. Ist dort eine URL eingetragen, sendet Discord Klicks auf Schaltflächen an sie statt an TaskHub, und TaskHub warnt dich.

## In TaskHub einfügen
1. TaskHub → **Einstellungen** → **Discord**. Füge das Token ein und klicke auf **Verbinden**. Ein Token, das Discord ablehnt, wird nicht gespeichert.
2. Füge den Bot einem Server hinzu (nächster Abschnitt). Discord erlaubt einem Bot nur, jemandem zu schreiben, mit dem er einen Server teilt; bis dahin sagt TaskHub, dass der Bot dir noch nicht schreiben kann.
3. Klicke auf **Erneut prüfen**. Der Bot schickt dir eine Direktnachricht mit einer Schaltfläche **Verbinden**. Drücke sie.

Verknüpft werden kann nur der Besitzer der Anwendung (bei der Anwendung eines Teams der Besitzer des Teams). Nur die Klicks dieser Person in diesem Chat bewirken etwas.

## Den Bot einem Server hinzufügen
1. Klicke in TaskHub auf **Bot zu einem Server hinzufügen**. Die Seite von Discord öffnet sich im Browser.
2. Wähle in der Serverliste einen, auf dem du Bots hinzufügen darfst: Er gehört dir, oder du hast dort **Manage Server**. Der Bot bekommt dort keine Berechtigungen und liest nichts mit; er muss nur einen Server mit dir teilen.
3. Bestätige auf dieser Seite, geh dann zurück zu TaskHub und klicke auf **Erneut prüfen**.

**Hast du keinen eigenen Server?** Einen zu erstellen ist kostenlos: Klicke in der Discord-App unten in der linken Seitenleiste auf **Add a Server**, wähle **Create My Own**, dann **For me and my friends**, gib einen Namen ein und klicke auf **Create**. Wiederhole danach die obigen Schritte.

**Immer noch keine Nachricht?** Vielleicht blockieren deine Privatsphäre-Einstellungen sie: Klicke auf den Namen des Servers, öffne **Privacy Settings**, schalte **Allow direct messages from server members** ein und klicke auf **Erneut prüfen**.

## Wo das Token gespeichert wird
- Nur in der Windows-Anmeldeinformationsverwaltung, nie in `config.json`. Ist die Anmeldeinformationsverwaltung nicht verfügbar, sagt TaskHub das und verbindet sich nicht.
- Es wird nur an `discord.com` und an das Gateway von Discord (`*.discord.gg`) gesendet.
- Deine Discord-Benutzer-ID, die ID des Chats und die ID der Anwendung werden in `config.json` gespeichert.

## Widerrufen
- **Bei Discord:** Developer Portal → deine Anwendung → **Bot** → **Reset Token**. Das alte Token funktioniert nicht mehr.
- **In TaskHub:** Einstellungen → Discord → **Trennen** (löscht es aus der Anmeldeinformationsverwaltung). Der Bot bleibt in deinem Discord-Konto, bis du die Anwendung löschst. Die Deinstallation von TaskHub mit **Auch meine Daten löschen** löscht auch das Token; eine einfache Deinstallation behält es.

## Wenn das Token nicht mehr funktioniert
Discord setzt das Token eines Bots nach 1000 neuen Verbindungen an einem Tag zurück und schreibt dem Besitzer eine E-Mail. TaskHub bleibt weit darunter und wartet, wenn es sich der Grenze nähert. Passiert es trotzdem, steht auf der Seite, dass das Token abgelehnt wurde: Erstelle ein neues und verbinde dich erneut.

## Offizielle Dokumentation
- Erste Schritte: <https://docs.discord.com/developers/quick-start/getting-started>
- Bots und Direktnachrichten, gemeinsame Server: <https://docs.discord.com/developers/interactions/application-commands>
- Einen Server erstellen: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Direktnachrichten von Servermitgliedern: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Wer einen Bot hinzufügen darf (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL und Gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Verbindungslimits: <https://docs.discord.com/developers/events/gateway>
- Datenschutzerklärung: <https://discord.com/privacy>
