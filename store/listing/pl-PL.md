# pl-PL

## Description

TaskHub zbiera w jednym miejscu długo trwające zadania na Twoim komputerze i pozwala nimi sterować: eksport wideo, renderowanie, kompilacje, kopie zapasowe, trenowanie modeli oraz skrypty, które uruchamiają dla Ciebie agenci AI do programowania.

Jak to działa
Program lub skrypt zgłasza TaskHub swoje postępy przez lokalne API HTTP na 127.0.0.1, polecenie taskhub albo dołączonego klienta Python. TaskHub pokazuje:
• ikonę w zasobniku z pierścieniem postępu i liczbą aktywnych zadań;
• panel z postępem, pozostałym czasem, stanem i dziennikiem każdego zadania oraz widżetami, o które zadanie prosi: wykresy liniowe, warstwowe i słupkowe, wskaźniki, liczby, kontrolki, tabele;
• własne elementy sterujące zadania: przyciski, pola liczbowe, tekstowe i listy, pauzę, zatrzymanie i zakończenie. Twój wybór wraca do zadania;
• powiadomienia Windows, gdy zadanie się zakończy, nie powiedzie albo przestanie się zgłaszać – z przyciskami zadania, jeśli o nie prosi.

„taskhub run -- <polecenie>” obejmuje dowolny wiersz poleceń: przekazuje wyjście do dziennika zadania, odczytuje z niego postęp i dodaje przyciski Zatrzymaj i Zakończ.

Claude Code i Codex
W menu w zasobniku (Agenci AI) możesz połączyć TaskHub z Claude Code i Codex. Agent dostaje narzędzia TaskHub (MCP), aby zgłaszać swoje długie zadania, a Ty dostajesz powiadomienie, gdy agent skończy odpowiedź albo czeka na Twoją zgodę. TaskHub dopisuje swoje wpisy do plików ustawień agentów i usuwa je, gdy wyłączysz tę opcję.

Telegram (opcjonalnie)
Podłącz własnego bota Telegram, aby dostawać te same powiadomienia na telefonie i stamtąd naciskać przyciski zadań. Wiadomości trafiają z Twojego komputera bezpośrednio do Bot API Telegrama.

Prywatność
Bez konta, bez usługi w chmurze, bez telemetrii. API nasłuchuje tylko na 127.0.0.1, a zadania i ustawienia zostają w %APPDATA%\TaskHub.

Warto wiedzieć
• Aktualizacje przychodzą z Microsoft Store.
• Autostart można wyłączyć w menu w zasobniku albo w Ustawienia → Aplikacje → Uruchamianie.
• Dokumentację API otwiera się z menu w zasobniku (Dokumentacja API).
• Wymaga Windows 10 w wersji 1809 lub nowszej (x64) oraz Microsoft Edge WebView2 Runtime, który jest już w aktualnym Windows.
• Języki interfejsu: angielski, ukraiński, hiszpański, niemiecki, francuski, włoski, portugalski, polski, chiński (uproszczony), japoński.

## Product features

- Ikona w zasobniku z pierścieniem postępu i liczbą aktywnych zadań
- Panel z postępem, pozostałym czasem, dziennikiem, wykresami, wskaźnikami i tabelami dla każdego zadania
- Przyciski i pola, które deklaruje samo zadanie: pauza, zatrzymanie, zmiana parametru, zakończenie
- Powiadomienia Windows o zakończeniu, błędzie i zawieszeniu, z przyciskami zadania
- Lokalne API HTTP, polecenie taskhub i klient Python; „taskhub run” obejmuje dowolne polecenie
- Claude Code i Codex: narzędzia TaskHub (MCP) i powiadomienia, włączane z menu w zasobniku
- Opcjonalny bot Telegram: powiadomienia, postęp i przyciski zadania na telefonie
- Bez konta, bez chmury, bez telemetrii; API nasłuchuje tylko na 127.0.0.1
- 10 języków interfejsu

## Short description

Śledź długo trwające zadania na komputerze i steruj nimi: postęp, wykresy, powiadomienia i własne przyciski każdego zadania. Działa z dowolnym skryptem przez lokalne API lub polecenie taskhub, a także z Claude Code i Codex.
