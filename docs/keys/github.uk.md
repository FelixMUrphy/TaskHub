# GitHub Actions: токен для TaskHub

[English](github.md) · **Українська** · [Español](github.es.md) · [Deutsch](github.de.md) · [Français](github.fr.md) · [Italiano](github.it.md) · [Português](github.pt.md) · [Polski](github.pl.md) · [中文](github.zh.md) · [日本語](github.ja.md) · [Усі сторінки](README.md)

## Навіщо він TaskHub
- **Спостереження:** запуски workflow вибраних репозиторіїв — прогрес, крок, що впав, результат.
- **Дії** (лише з правом на запис): Cancel і Re-run запуску.
- **Pull requests** (необов'язково, з 2.9.4, лише читання): ті, що чекають вашого рев'ю, і відкриті PR від Dependabot.
- TaskHub ніколи не змінює ваші workflow, код чи налаштування репозиторіїв.

## Створити fine-grained токен
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (пряме посилання: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** наприклад, `TaskHub`.
3. **Expiration:** за замовчуванням 30 днів; виберіть зручний строк (TaskHub попередить за тиждень до закінчення).
4. **Resource owner:** ви або організація, якій належать репозиторії.
5. **Repository access:** **Only select repositories** → репозиторії, які ви вкажете в TaskHub.
6. **Permissions** (див. нижче) → **Generate token** → скопіюйте його.

Якщо власник — організація, що вимагає схвалення, токен чекатиме, доки адміністратор його не схвалить.

## Мінімальні дозволи
| Потрібно | Repository permissions |
|---|---|
| (a) Лише спостерігати | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Cancel / Re-run | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull requests (необов'язково, з 2.9.4, лише читання): для PR від Dependabot додайте ще **Pull requests: Read-only** (GitHub
дає список відкритих PR лише з цим дозволом); пошук запитів на рев'ю дозволів не потребує. Fine-grained токен бачить
запити на рев'ю лише в наданих йому репозиторіях і публічних.

Краще цей токен, ніж інший варіант у TaskHub (токен, який уже має Git Credential Manager), — той має доступ до
всіх ваших репозиторіїв.

## Стежити ще за одним репозиторієм пізніше
З режимом **Only select repositories** токен бачить лише вибрані репозиторії. Якщо TaskHub пише, що репозиторій
*не знайдено, або токен не має доступу*: перевір назву, потім відкрий токен на GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) і додай репозиторій у **Repository access**. GitHub відповідає однаково на неправильну назву,
на репозиторій, якого токену не надали, і подекуди на відсутній дозвіл, тому TaskHub не може сказати, що саме сталося.
[GitHub: 404 для наявного ресурсу](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Вставити в TaskHub
1. TaskHub → **Налаштування** → **Конектори** → **GitHub Actions**.
2. Увімкніть, виберіть **Власний fine-grained токен (рекомендовано)**, вставте токен, вкажіть репозиторії
   (`owner/repo`), натисніть **Зберегти**.
3. Має з’явитися **Під’єднано як** *ваш логін*. Якщо GitHub не прийме токен, нічого не збережеться.

## Де зберігається токен
- Лише в диспетчері облікових даних Windows (Credential Manager) — ніколи в `config.json`.
- Надсилається лише на `api.github.com`. Необов’язковий хвіст логу завантажується за одноразовим посиланням від
  GitHub, без токена.

## Відкликати
- **У GitHub:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → токен →
  **Delete**.
- **У TaskHub:** Налаштування → Конектори → GitHub Actions → **Видалити** (стирає токен із диспетчера облікових даних).

## Офіційна документація
- Створення fine-grained токена:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Пошук pull request'ів (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Дозволи для кожного endpoint:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
