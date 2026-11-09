# Vast.ai: klucz API dla TaskHub

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · [Deutsch](vastai.de.md) · [Français](vastai.fr.md) · [Italiano](vastai.it.md) · [Português](vastai.pt.md) · **Polski** · [中文](vastai.zh.md) · [日本語](vastai.ja.md) · [Wszystkie strony](README.md)

## Do czego TaskHub go używa
- **Obserwowanie:** Twoje instancje, ich obciążenie, cena, opłaty z ostatnich 30 dni, saldo i kredyt.
- **Akcje** (tylko jeśli klucz na nie pozwala): Zatrzymaj, Uruchom, Zrestartuj — z panelu, powiadomienia lub Telegrama.
- **Usuwanie (destroy)** nigdy nie dzieje się automatycznie. Działa tylko w panelu TaskHub, po wpisaniu nazwy.

## Minimalne uprawnienia
| Co chcesz | Uprawnienia |
|---|---|
| (a) Tylko obserwować | `instance_read`, `user_read`, `billing_read` |
| (b) Zatrzymaj / Uruchom / Zrestartuj | powyższe + `instance_write` |

Uwaga: `instance_write` w Vast.ai obejmuje „creating, managing, and destroying instances and volumes” — nie ma
uprawnienia do zatrzymywania / uruchamiania bez możliwości usuwania. TaskHub nigdy nie usuwa sam, tylko z panelu, po
wpisaniu nazwy.

## Utwórz klucz
**W konsoli:** otwórz sekcję **Keys** w konsoli Vast.ai → **API Keys** → **+New**. W oknie podaj
nazwę (np. `TaskHub`) i wybierz uprawnienia — domyślnie zaznaczone są wszystkie uprawnienia Twojego konta,
więc zostaw tylko te z tabeli. Utwórz klucz i skopiuj go.

**Przez CLI:** strony Vast.ai o uprawnieniach opisują klucze z ograniczeniami przez CLI / API — użyj tego, jeśli
konsola nie pozwala ich zawęzić. Zapisz jako `perms.json`:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Dodaj `"instance_write": {}` dla akcji, a potem uruchom:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## Wklej go do TaskHub
1. TaskHub → **Ustawienia** → **Konektory** → **Vast.ai**.
2. Włącz konektor, wklej klucz i kliknij **Zapisz**.
3. W wierszu powinno być **Połączono, saldo …** (albo **Połączono**). Jeśli Vast.ai odrzuci klucz, nic nie zostanie zapisane.

TaskHub sprawdza raz (a potem codziennie), czy klucz może wykonywać akcje, i pokazuje przyciski akcji tylko wtedy, gdy może.

## Gdzie klucz jest przechowywany
- Tylko w Menedżerze poświadczeń Windows — nigdy w `config.json`.
- Wysyłany tylko do `console.vast.ai`.

## Odwołaj go
- **W Vast.ai:** konsola → **Keys** → **Delete** przy kluczu (**Reset** zastępuje go nowym).
- **W TaskHub:** Ustawienia → Konektory → Vast.ai → **Usuń** (usuwa go z Menedżera poświadczeń).

## Oficjalna dokumentacja
- API keys: <https://docs.vast.ai/guides/reference/keys>
- Permissions: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Billing and balance: <https://docs.vast.ai/guides/reference/billing>
