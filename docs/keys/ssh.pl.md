# SSH: konfiguracja hosta dla TaskHub

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · [Deutsch](ssh.de.md) · [Français](ssh.fr.md) · [Italiano](ssh.it.md) · [Português](ssh.pt.md) · **Polski** · [中文](ssh.zh.md) · [日本語](ssh.ja.md) · [Wszystkie strony](README.md)

## Co TaskHub z tym robi
- TaskHub uruchamia systemowy `ssh.exe` z Windows i odczytuje każdy wskazany host: obciążenie, pamięć, dyski, GPU, sesje tmux i proces, który wskażesz.
- **TaskHub nie przechowuje żadnego klucza ani hasła.** Korzysta z Twojego `~/.ssh/config`, agenta SSH i `known_hosts` i tylko je odczytuje.
- Niczego nie instaluje na hoście i niczego tam nie zmienia. Na razie działa z hostami z Linuksem.

## Zanim dodasz host
TaskHub nie potrafi odpowiadać na pytania, więc trzy rzeczy muszą działać bez nich.

1. **Klient OpenSSH.** TaskHub używa tego z Windows, a nie kopii z Gita. W terminalu `C:\Windows\System32\OpenSSH\ssh.exe -V` powinno wypisać wersję. Jeśli nie: Ustawienia systemu Windows → System → Funkcje opcjonalne (w Windows 10: Aplikacje → Funkcje opcjonalne) → dodaj *Klient OpenSSH* ([kroki od Microsoftu](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **Klucz bez wpisywania.** Klucz z hasłem musi być w agencie. W PowerShellu jako administrator, jednorazowo:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Potem, jako Ty: `ssh-add`. ([Poradnik Microsoftu o kluczach](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Agenci z 1Password lub Pageant działają, jeśli obsługują OpenSSH z Windows.
3. **Klucz hosta jest zaufany.** Połącz się raz z terminala: `ssh my-server`. Porównaj pokazany odcisk (fingerprint) z tym od właściciela hosta i odpowiedz `yes`. TaskHub nigdy sam nie zaufa nowemu kluczowi.

Sprawdzian: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` wypisuje `Linux` bez żadnych pytań.

Nazwa hosta trafia do `~/.ssh/config`, razem z użytkownikiem, portem i kluczem:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## Dodaj go
Ustawienia → *Konektory* → *SSH* → włącz, wpisz `my-server` i naciśnij *Dodaj*. Tylko litery, cyfry, kropki, myślniki i podkreślenia: `user@host` wpisz w pliku config.

## Co zobaczysz
Zadanie dla każdego hosta, z jego obciążeniem i dyskami. Dostaniesz powiadomienie, gdy dysk się zapełni, gdy połączenie zostanie utracone albo TaskHub będzie musiał przestać próbować, gdy host się zrestartuje i gdy zakończy się zadanie:
- **sesja tmux** albo polecenie w niej, które wraca do powłoki (zamknięcie edytora też się liczy; przełącznik w Ustawieniach to wyłącza, a zdarzenia zostają w logu zadania);
- **proces, który wskażesz:** *Obserwuj proces* przy zadaniu hosta, z jego numerem (na przykład `pgrep -f train`).

TaskHub nie potrafi stwierdzić, jak zadanie się skończyło, więc pisze „zakończone”, a nie „udane”. Gdy Twój komputer jest wyłączony, nikt nie obserwuje. Proces, który wskazałeś, a który w międzyczasie się zakończył, zostanie zgłoszony przy następnym połączeniu. Zmiany w tmux i restart hosta są zauważane tylko przy utracie połączenia, gdy TaskHub działa: nie po restarcie TaskHub ani komputera.

## Co warto wiedzieć
- **Twój config jest wykonywany.** `ProxyCommand` lub `Match exec` w `~/.ssh/config` uruchamia się przy każdym połączeniu TaskHub, tak jak przy Twoim połączeniu z terminala. Hosty za `ProxyJump` używają ustawień z Twojego configu dla hosta pośredniczącego.
- **Co zostaje otwarte.** Jeden `ssh` na host, dopóki konektor jest włączony. Co minutę domyślnie (najrzadziej co 30 sekund) wysyła stały skrypt tylko do odczytu.
- **Zmieniony klucz zatrzymuje połączenie.** Jeśli klucz hosta się zmieni, TaskHub nie połączy się i Cię o tym poinformuje. Może to być zainstalowany od nowa host albo ktoś pośrodku: sprawdź, a potem sam popraw `known_hosts`.
- **Nieudane logowania.** Po odrzuconym kluczu TaskHub przestaje próbować, dopóki nie naciśniesz *Sprawdź ponownie*, żeby nie doprowadzić do zbanowania Twojego adresu.
- **Co opuszcza Twój komputer.** Tylko do Twoich hostów: Twoja nazwa użytkownika i klucze publiczne (jak przy każdym ssh) oraz skrypt. To, co wraca, zostaje w folderze danych TaskHub i pojawia się w powiadomieniach, także w Telegramie lub Discordzie, gdy są włączone. Zobacz [PRIVACY.md](../../PRIVACY.md).

## Zatrzymaj
Wyłącz konektor albo usuń host: połączenie się kończy. Nic nie zostaje na hoście i w TaskHub nie ma klucza do usunięcia.
