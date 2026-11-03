# Runpod: klucz API dla TaskHub

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · [Deutsch](runpod.de.md) · [Français](runpod.fr.md) · [Italiano](runpod.it.md) · [Português](runpod.pt.md) · **Polski** · [中文](runpod.zh.md) · [日本語](runpod.ja.md) · [Wszystkie strony](README.md)

## Do czego TaskHub go używa
- **Obserwowanie:** Twoje pody, ich obciążenie, cena, dotychczasowe wydatki (rozliczenia Runpod), saldo i na jak długo go wystarczy.
- **Akcje** (tylko jeśli klucz na nie pozwala): Zatrzymaj, Uruchom, Uruchom ponownie — z panelu, powiadomienia lub Telegrama.
- **Usuwanie** nigdy nie dzieje się automatycznie. Działa tylko w panelu TaskHub, po wpisaniu nazwy poda.

## Utwórz klucz
1. Otwórz konsolę Runpod: <https://console.runpod.io/user/credentials>.
2. W sekcji **API Keys** kliknij **Create API Key**.
3. Wpisz nazwę (np. `TaskHub`) i wybierz typ (patrz niżej).
4. Kliknij **Create** i od razu skopiuj klucz — jest pokazywany tylko raz; Runpod go nie przechowuje.

## Jaki typ wybrać
| Co chcesz | Typ | Uwagi |
|---|---|---|
| (a) Tylko obserwować | **Read Only** | Zalecany, jeśli nie potrzebujesz przycisków. Runpod nie opisuje, czy klucz Read Only może czytać rozliczenia i saldo; jeśli TaskHub nie pokazuje salda, to jest powód. |
| (b) Zatrzymaj / Uruchom / Uruchom ponownie | **All** | Runpod opisuje zakresy **Restricted** tylko dla endpointów Serverless, nie dla podów — więc do akcji na podach użyj **All**. |

TaskHub sprawdza raz (a potem codziennie), czy klucz może wykonywać akcje, i pokazuje przyciski akcji tylko wtedy, gdy może.
Klucz typu **All** mógłby też usuwać pody — TaskHub nigdy nie robi tego sam, tylko z panelu, po
wpisaniu nazwy.

## Wklej go do TaskHub
1. TaskHub → **Ustawienia** → **Konektory** → **Runpod**.
2. Włącz konektor, wklej klucz i kliknij **Zapisz**.
3. W wierszu powinno być **Połączono, saldo …** (albo **Połączono**). Jeśli Runpod odrzuci klucz, nic nie zostanie zapisane.

## Gdzie klucz jest przechowywany
- Tylko w Menedżerze poświadczeń Windows — nigdy w `config.json`.
- Wysyłany tylko do `api.runpod.io` (REST dla podów, rozmiarów woluminów sieciowych i rozliczeń, GraphQL dla salda).

## Odwołaj go
- **W Runpod:** <https://console.runpod.io/user/credentials> → ikona kosza przy kluczu → **Revoke Key**
  (albo wyłącz klucz jego przełącznikiem).
- **W TaskHub:** Ustawienia → Konektory → Runpod → **Usuń** (usuwa go z Menedżera poświadczeń).
  Odinstalowanie TaskHub z opcją **Usuń też moje dane** też go usuwa; zwykłe odinstalowanie go zostawia.

## Oficjalna dokumentacja
- API keys: <https://docs.runpod.io/get-started/credentials>
- Rate limits: <https://docs.runpod.io/api-reference-v2/rate-limits>
- Pricing, and what happens at $0: <https://docs.runpod.io/pods/pricing>
