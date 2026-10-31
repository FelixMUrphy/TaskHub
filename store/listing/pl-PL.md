# pl-PL

## Description

TaskHub zbiera w jednym miejscu długo trwające zadania na Twoim komputerze i pozwala nimi sterować: eksport wideo, renderowanie, kompilacje, kopie zapasowe, trenowanie modeli, przebiegi CI, maszyny GPU w chmurze oraz skrypty, które uruchamiają dla Ciebie agenci AI do programowania.

Jak to działa
Program lub skrypt zgłasza TaskHub swoje postępy przez lokalne API HTTP na 127.0.0.1, polecenie taskhub albo dołączonego klienta Python. TaskHub pokazuje:
• ikonę w zasobniku z pierścieniem postępu i liczbą aktywnych zadań;
• panel z postępem, pozostałym czasem, stanem i dziennikiem każdego zadania oraz widżetami, o które zadanie prosi: wykresy liniowe, warstwowe i słupkowe, wskaźniki, liczby, kontrolki, tabele;
• własne elementy sterujące zadania: przyciski, pola liczbowe, tekstowe i listy, pauzę, zatrzymanie i zakończenie. Twój wybór wraca do zadania;
• powiadomienia Windows, gdy zadanie się zakończy, nie powiedzie albo przestanie się zgłaszać – z przyciskami zadania, jeśli o nie prosi.

„taskhub run -- <polecenie>” obejmuje dowolny wiersz poleceń: przekazuje wyjście do dziennika zadania, odczytuje z niego postęp i dodaje przyciski Zatrzymaj i Zakończ.

Ustawienia w panelu
Wszystko ustawia się na jednej stronie: przycisk ⚙ w panelu (albo Ustawienia w menu w zasobniku) otwiera wygląd, powiadomienia i Telegram, konektory, agentów AI, autostart i dokumentację API. W menu w zasobniku zostają szybkie akcje: otwórz panel, wycisz, ustawienia, wsparcie, zakończ.

Claude Code i Codex
W Ustawienia → Agenci AI możesz połączyć TaskHub z Claude Code i Codex. Agent dostaje narzędzia TaskHub (MCP), aby zgłaszać swoje długie zadania, a Ty dostajesz powiadomienie, gdy agent skończy odpowiedź albo czeka na Twoją zgodę. TaskHub dopisuje swoje wpisy do plików ustawień agentów i usuwa je, gdy wyłączysz tę opcję.

Konektory (opcjonalne, domyślnie wyłączone)
• GitHub Actions: przebiegi workflow we wskazanych repozytoriach jako zadania: postęp według jobów i kroków, krok, który się nie powiódł, i koniec jego dziennika, przyciski anulowania i ponownego uruchomienia.
• Runpod i Vast.ai: Twoje maszyny GPU jako zadania: GPU, cena za godzinę, dotychczasowy koszt, czas działania, SSH, wykresy obciążenia, ostrzeżenie „GPU bezczynne”, saldo i na jak długo wystarczy, z ostrzeżeniem o niskim saldzie, zatrzymanie / uruchomienie / restart (usuwanie tylko w panelu, po wpisaniu nazwy maszyny).
Każdy konektor działa dopiero, gdy go włączysz i podasz własny klucz API. Klucz jest przechowywany wyłącznie w Menedżerze poświadczeń Windows, a konektor łączy się tylko z API własnej usługi (api.github.com, api.runpod.io, console.vast.ai).

Ochrona salda (opcjonalna, wyłączona, dopóki jej nie włączysz)
Dla Runpod i Vast.ai: ostrzeżenie przy Twoim progu salda, a przy progu zatrzymania działające maszyny są zatrzymywane po powiadomieniu z przyciskami Zatrzymaj teraz i Odłóż. Ochrona tylko zatrzymuje maszyny i nigdy ich nie usuwa; jeśli nie da się odczytać salda, niczego nie zatrzymuje.

Telegram (opcjonalnie)
Podłącz własnego bota Telegram, aby dostawać na telefonie te same powiadomienia i aktualizowaną na żywo wiadomość z postępem każdego zadania oraz stamtąd naciskać przyciski zadań. Token bota jest przechowywany w Menedżerze poświadczeń Windows, a wiadomości trafiają z Twojego komputera bezpośrednio do Bot API Telegrama.

Prywatność
Bez konta, bez telemetrii. API nasłuchuje tylko na 127.0.0.1, a zadania i ustawienia zostają w %APPDATA%\TaskHub. Nic nie opuszcza Twojego komputera, dopóki nie włączysz Telegrama albo konektora.

Warto wiedzieć
• Aktualizacje przychodzą z Microsoft Store.
• Autostart można wyłączyć w Ustawienia → System albo w Ustawieniach Windows → Aplikacje → Uruchamianie.
• Dokumentację API otwiera się ze strony ustawień.
• Wymaga Windows 10 w wersji 2004 lub nowszej (x64) oraz Microsoft Edge WebView2 Runtime, który jest już w aktualnym Windows.
• Języki interfejsu: angielski, ukraiński, hiszpański, niemiecki, francuski, włoski, portugalski, polski, chiński (uproszczony), japoński.

## What's new in this version

TaskHub 2.9
• Strona ustawień w panelu (⚙): wszystko w jednym miejscu; menu w zasobniku zachowuje szybkie akcje, w stylu Windows 11.
• Nowe konektory, domyślnie wyłączone: GitHub Actions (przebiegi workflow z anulowaniem i ponownym uruchomieniem), Runpod i Vast.ai (maszyny GPU, koszty, saldo, ostrzeżenie o bezczynnym GPU, zatrzymanie i uruchomienie). Twoje klucze API są przechowywane wyłącznie w Menedżerze poświadczeń Windows.
• Ochrona salda dla Runpod i Vast.ai, wyłączona, dopóki jej nie włączysz: ostrzeżenie przy Twoim progu, a przy progu zatrzymania działające maszyny są zatrzymywane, nigdy usuwane.
• Telegram: token bota przenosi się do Menedżera poświadczeń Windows; TaskHub trzyma się tempa wiadomości Telegrama zamiast je gubić.
• Ustawienie przezroczystości kart; linki do plików i folderów są pokazywane w Eksploratorze, nigdy uruchamiane.
• 2.9.1: kwoty pieniędzy z dwoma miejscami po przecinku, koszt przechowywania network volume w Runpod.
• Teraz wymaga Windows 10 w wersji 2004 lub nowszej.

## Product features

- Ikona w zasobniku z pierścieniem postępu i liczbą aktywnych zadań
- Panel z postępem, pozostałym czasem, dziennikiem, wykresami, wskaźnikami i tabelami dla każdego zadania
- Przyciski i pola, które deklaruje samo zadanie: pauza, zatrzymanie, zmiana parametru, zakończenie
- Powiadomienia Windows o zakończeniu, błędzie i zawieszeniu, z przyciskami zadania
- Lokalne API HTTP, polecenie taskhub i klient Python; „taskhub run” obejmuje dowolne polecenie
- Wszystkie ustawienia na jednej stronie w panelu (⚙); szybkie akcje w menu w zasobniku
- Claude Code i Codex: narzędzia TaskHub (MCP) i powiadomienia, włączane w ustawieniach
- Opcjonalny konektor GitHub Actions: przebiegi workflow jako zadania, z anulowaniem i ponownym uruchomieniem
- Opcjonalne konektory Runpod i Vast.ai: maszyny GPU, koszty, saldo, ostrzeżenie o bezczynnym GPU, zatrzymanie i uruchomienie
- Opcjonalna ochrona salda: ostrzega przy niskim saldzie i zatrzymuje (nigdy nie usuwa) działające maszyny
- Opcjonalny bot Telegram: powiadomienia, postęp i przyciski zadania na telefonie
- Bez konta, bez telemetrii; klucze API tylko w Menedżerze poświadczeń Windows; API nasłuchuje tylko na 127.0.0.1
- 10 języków interfejsu

## Short description

Śledź długo trwające zadania na komputerze i steruj nimi: postęp, wykresy, powiadomienia i własne przyciski każdego zadania. Działa z dowolnym skryptem przez lokalne API lub polecenie taskhub, z Claude Code i Codex, a opcjonalnie z GitHub Actions, Runpod i Vast.ai.
