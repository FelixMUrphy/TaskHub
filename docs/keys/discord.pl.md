# Discord: token bota dla TaskHub

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · [Français](discord.fr.md) · [Italiano](discord.it.md) · [Português](discord.pt.md) · **Polski** · [中文](discord.zh.md) · [日本語](discord.ja.md) · [Wszystkie strony](README.md)

## Do czego TaskHub go używa
- Wysyła Twoje powiadomienia TaskHub, postęp zadań i wykresy jako wiadomości prywatne od **Twojego własnego** bota.
- Odbiera naciśnięcia Twoich przycisków oraz polecenia `/tasks`, `/mute`, `/unmute` przez połączenie, które Twój komputer sam otwiera do Discorda. Publiczny adres nie jest potrzebny.
- Nie czyta Twoich wiadomości: nie prosi o żadne uprzywilejowane intenty i nigdy o treść wiadomości.

## Utwórz bota
1. Otwórz Discord Developer Portal: <https://discord.com/developers/applications?new_application=true>.
2. Kliknij **Create App**, wpisz nazwę (np. `TaskHub`) i kliknij **Create**.
3. Na stronie **Bot**, w sekcji **Token**, kliknij **Reset Token** i skopiuj token. Discord pokazuje go tylko raz.
4. Zachowaj bota dla siebie: na stronie **Installation** ustaw **Install Link** na **None**, a potem na stronie **Bot** wyłącz **Public Bot**. Przy wyłączonej opcji tylko Ty możesz dodać bota do serwerów. (Jeśli Portal odmawia, zrób najpierw krok z **Installation**.)
5. Resztę zostaw bez zmian: bez uprzywilejowanych intentów i z pustym **Interactions Endpoint URL**. Gdy jest tam adres, Discord wysyła naciśnięcia przycisków na niego zamiast do TaskHub, a TaskHub Cię o tym ostrzega.

## Wklej go do TaskHub
1. TaskHub → **Ustawienia** → **Komunikatory** → **Discord**. Wklej token i kliknij **Połącz**. Token, który Discord odrzuci, nie zostanie zapisany.
2. Dodaj bota do serwera (następna sekcja). Discord pozwala botowi pisać tylko do osoby, z którą ma wspólny serwer, więc do tego czasu TaskHub informuje, że bot jeszcze nie może do Ciebie pisać.
3. Kliknij **Sprawdź ponownie**. Bot wyśle Ci wiadomość prywatną z przyciskiem **Połącz**. Naciśnij go.

Połączyć można tylko właściciela aplikacji (w przypadku aplikacji zespołu — właściciela zespołu). Tylko naciśnięcia tej osoby, w tym czacie, coś robią.

## Dodaj bota do serwera
1. W TaskHub kliknij **Dodaj bota do serwera**. W przeglądarce otworzy się strona Discorda.
2. Na liście serwerów wybierz taki, na którym możesz dodawać boty: jesteś jego właścicielem lub masz tam uprawnienie **Manage Server**. Bot nie dostaje w nim żadnych uprawnień i go nie czyta; potrzebuje tylko wspólnego z Tobą serwera.
3. Potwierdź na tej stronie, wróć do TaskHub i kliknij **Sprawdź ponownie**.

**Nie masz własnego serwera?** Utworzenie go jest darmowe: w aplikacji Discord kliknij **Add a Server** na dole lewego paska bocznego, wybierz **Create My Own**, potem **For me and my friends**, wpisz nazwę i kliknij **Create**. Potem powtórz powyższe kroki.

**Nadal brak wiadomości?** Mogą ją blokować Twoje ustawienia prywatności: kliknij nazwę serwera, otwórz **Privacy Settings**, włącz **Allow direct messages from server members** i kliknij **Sprawdź ponownie**.

## Gdzie token jest przechowywany
- Tylko w Menedżerze poświadczeń Windows, nigdy w `config.json`. Jeśli Menedżer poświadczeń jest niedostępny, TaskHub informuje o tym i się nie łączy.
- Wysyłany tylko do `discord.com` i bramy Discorda (`*.discord.gg`).
- Twój identyfikator użytkownika Discord, identyfikator czatu i identyfikator aplikacji są zapisane w `config.json`.

## Odwołaj go
- **W Discordzie:** Developer Portal → Twoja aplikacja → **Bot** → **Reset Token**. Stary token przestaje działać.
- **W TaskHub:** Ustawienia → Komunikatory → Discord → **Rozłącz** (usuwa go z Menedżera poświadczeń). Bot zostaje na Twoim koncie Discord, dopóki nie usuniesz aplikacji. Odinstalowanie TaskHub z opcją **Usuń też moje dane** usuwa też token; zwykłe odinstalowanie go zostawia.

## Jeśli token przestanie działać
Discord resetuje token bota po 1000 nowych połączeń w ciągu doby i wysyła właścicielowi e-mail. TaskHub trzyma się dużo poniżej tego limitu i czeka, gdy się do niego zbliża. Jeśli mimo to się zdarzy, strona napisze, że token został odrzucony: utwórz nowy i połącz się ponownie.

## Oficjalna dokumentacja
- Getting started: <https://docs.discord.com/developers/quick-start/getting-started>
- Bots and direct messages, mutual servers: <https://docs.discord.com/developers/interactions/application-commands>
- Creating a server: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Direct messages from server members: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Who can add a bot (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL vs the gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Connection limits: <https://docs.discord.com/developers/events/gateway>
- Privacy policy: <https://discord.com/privacy>
