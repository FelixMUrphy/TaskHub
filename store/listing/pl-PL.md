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

Ustawienia
Przycisk ⚙ w panelu (albo Ustawienia w menu w zasobniku) otwiera ustawienia obok panelu, z kartami: wygląd, powiadomienia i Telegram, konektory, agenci AI, autostart i dokumentacja API. W menu w zasobniku zostają szybkie akcje: otwórz panel, wycisz, ustawienia, wsparcie, zakończ.

Claude Code i Codex
W Ustawienia → Agenci AI możesz połączyć TaskHub z Claude Code i Codex. Agent dostaje narzędzia TaskHub (MCP), aby zgłaszać swoje długie zadania, a Ty dostajesz powiadomienie, gdy agent skończy odpowiedź albo czeka na Twoją zgodę. TaskHub dopisuje swoje wpisy do plików ustawień agentów i usuwa je, gdy wyłączysz tę opcję. Możesz też włączyć limity planu: ile wykorzystano z limitów Twojego planu Claude Code i Codex, w panelu, z ostrzeżeniami przy 75% i 90%.

Konektory (opcjonalne, domyślnie wyłączone)
• GitHub Actions: przebiegi workflow we wskazanych repozytoriach jako zadania: postęp według jobów i kroków, krok, który się nie powiódł, i koniec jego dziennika, przyciski anulowania i ponownego uruchomienia.
• Runpod i Vast.ai: Twoje maszyny GPU jako zadania: GPU, cena za godzinę, dotychczasowy koszt, czas działania, SSH, wykresy obciążenia, ostrzeżenie „GPU bezczynne”, saldo i na jak długo wystarczy, z ostrzeżeniem o niskim saldzie, zatrzymanie / uruchomienie / restart (usuwanie tylko w panelu, po wpisaniu nazwy maszyny).
• AWS: instancje EC2, zadania treningowe SageMaker i zadania AWS Batch jako zadania, koszt i prognoza na ten miesiąc oraz AWS Budgets, każde z własnym przełącznikiem. Tylko odczyt. Cost Explorer jest płatny w AWS (0,01 USD za żądanie, najwyżej około 1,80 USD miesięcznie).
Każdy konektor działa dopiero, gdy go włączysz i podasz własny klucz API. Klucz jest przechowywany wyłącznie w Menedżerze poświadczeń Windows, a konektor łączy się tylko z API własnej usługi (api.github.com, api.runpod.io, console.vast.ai, *.amazonaws.com).

Ochrona salda (opcjonalna, wyłączona, dopóki jej nie włączysz)
Dla Runpod i Vast.ai: ostrzeżenie przy Twoim progu salda, a przy progu zatrzymania działające maszyny są zatrzymywane po powiadomieniu z przyciskami Zatrzymaj teraz i Odłóż. Ochrona tylko zatrzymuje maszyny i nigdy ich nie usuwa; jeśli nie da się odczytać salda, niczego nie zatrzymuje.

Telegram (opcjonalnie)
Podłącz własnego bota Telegram, aby dostawać na telefonie te same powiadomienia i aktualizowaną na żywo wiadomość z postępem każdego zadania oraz stamtąd naciskać przyciski zadań. Token bota jest przechowywany w Menedżerze poświadczeń Windows, a wiadomości trafiają z Twojego komputera bezpośrednio do Bot API Telegrama.

Prywatność
Bez konta, bez telemetrii. API nasłuchuje tylko na 127.0.0.1, a zadania i ustawienia zostają w %APPDATA%\TaskHub. Nic nie opuszcza Twojego komputera, dopóki nie włączysz Telegrama, konektora albo limitów planu Codex.

Warto wiedzieć
• Aktualizacje przychodzą z Microsoft Store.
• Autostart można wyłączyć w Ustawienia → System albo w Ustawieniach Windows → Aplikacje → Uruchamianie.
• Dokumentację API otwiera się ze strony ustawień.
• Wymaga Windows 10 w wersji 2004 lub nowszej (x64) oraz Microsoft Edge WebView2 Runtime, który jest już w aktualnym Windows.
• Języki interfejsu: angielski, ukraiński, hiszpański, niemiecki, francuski, włoski, portugalski, polski, chiński (uproszczony), japoński.

## What's new in this version

TaskHub 3.1
• Konektor AWS, domyślnie wyłączony, tylko odczyt: instancje EC2, zadania SageMaker i Batch, koszt miesiąca, Budgets.
• Limity planu dla Claude Code i Codex, wyłączone, dopóki ich nie włączysz: ile wykorzystano, z ostrzeżeniami przy 75% i 90%.
• Ustawienia otwierają się obok panelu, z kartami.
• Powiadomienia z Claude Code i Codex są proponowane w panelu, gdy agent zostanie znaleziony, i nie ma już dwóch powiadomień o tym samym.
• Czytelniejszy dziennik lokalny: tekst powiadomień i nazwy zadań są zapisywane tylko na poziomie debug.
• Działa na .NET 10. Przypięty panel trzyma swoją krawędź ekranu.
• Naprawiono: saldo Vast.ai; przycisk w Telegramie sprzed restartu mógł uruchomić przycisk innego zadania.

## Product features

- Ikona w zasobniku z pierścieniem postępu i liczbą aktywnych zadań
- Panel z postępem, pozostałym czasem, dziennikiem, wykresami, wskaźnikami i tabelami dla każdego zadania
- Przyciski i pola, które deklaruje samo zadanie: pauza, zatrzymanie, zmiana parametru, zakończenie
- Powiadomienia Windows o zakończeniu, błędzie i zawieszeniu, z przyciskami zadania
- Lokalne API HTTP, polecenie taskhub i klient Python; „taskhub run” obejmuje dowolne polecenie
- Ustawienia obok panelu (⚙), z kartami; szybkie akcje w menu w zasobniku
- Claude Code i Codex: narzędzia TaskHub (MCP) i powiadomienia, włączane w ustawieniach
- Opcjonalne limity planu Claude Code i Codex: ile wykorzystano, ostrzeżenia przy 75% i 90%
- Opcjonalny konektor GitHub Actions: przebiegi workflow jako zadania, z anulowaniem i ponownym uruchomieniem
- Opcjonalne konektory Runpod i Vast.ai: maszyny GPU, koszty, saldo, ostrzeżenie o bezczynnym GPU, zatrzymanie i uruchomienie
- Opcjonalny konektor AWS, tylko odczyt: instancje EC2, zadania SageMaker i Batch, koszt, Budgets
- Opcjonalna ochrona salda: ostrzega przy niskim saldzie i zatrzymuje (nigdy nie usuwa) działające maszyny
- Opcjonalny bot Telegram: powiadomienia, postęp i przyciski zadania na telefonie
- Bez konta, bez telemetrii; klucze API tylko w Menedżerze poświadczeń Windows; API nasłuchuje tylko na 127.0.0.1
- 10 języków interfejsu

## Short description

Śledź długo trwające zadania na komputerze i steruj nimi: postęp, wykresy, powiadomienia i własne przyciski każdego zadania. Działa z dowolnym skryptem przez lokalne API lub polecenie taskhub, z Claude Code i Codex, a opcjonalnie z GitHub Actions, Runpod, Vast.ai i AWS.
