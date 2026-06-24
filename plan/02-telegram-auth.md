# Этап 02 — Авторизация через Telegram

> **Зависит от:** этап 01 (скелет API + фронт + деплой).
> **Эталон:** в cashpulse — `AuthEndpoints.cs`, `Middleware/JwtMiddleware.cs`, `Core/Services/ITelegramAuthService.cs`, `Infrastructure/Services/TelegramAuthService.cs`, тест `tests/CashPulse.Tests/TelegramAuthServiceTests.cs`, helper `JwtTestHelper`. Зеркалировать паттерн.

## Цель этапа

Закрыть сервис **отдельной** Telegram-авторизацией (тот же бот, что у cashpulse), чтобы доступ был только у владельца. Единого SSO между сервисами нет — каждый сервис авторизуется самостоятельно. К концу этапа:
- вход через Telegram Login Widget → бэкенд валидирует подпись → выдаёт JWT;
- доступ только для `OWNER_TELEGRAM_ID` (allowlist); прочие — `403`;
- все доменные API-эндпоинты (кроме `/health` и самого логина) требуют валидный JWT;
- фронт хранит сессию, показывает экран логина и защищает роуты.

> Продукт одно-пользовательский (§2), поэтому авторизация = «пустить только владельца», а не мультитенантность. `UserId` владельца фиксируется при первом входе; мультисчёт/мультиюзер — точка расширения, не реализуем.

---

## Часть A — Backend

### A1. Валидация Telegram Login
Реализовать `TelegramAuthService` (`Bonds.Infrastructure/Services`, интерфейс в `Bonds.Core/Services`):
- Принимает payload Telegram Login Widget (`id`, `first_name`, `last_name`, `username`, `photo_url`, `auth_date`, `hash`).
- Проверяет подпись по алгоритму Telegram: secret key = `SHA256(bot_token)`; `data_check_string` из отсортированных полей; HMAC-SHA256 сверяется с `hash`.
- Проверяет свежесть `auth_date` (например, не старше 24 ч).
- Bot token из конфигурации `Telegram:BotToken`.
- Возвращает провалидированные данные пользователя либо ошибку.

### A2. Allowlist владельца
- Конфиг `Telegram:OwnerId` (= `OWNER_TELEGRAM_ID`).
- Если `id` из Telegram ≠ `OwnerId` → `403 Forbidden` (доступ только владельцу).
- При первом успешном входе владельца — создать/обновить запись в таблице `users` (см. этап 03; на этом этапе допустимо завести минимальную таблицу `users` миграцией здесь же: `id`, `telegram_id`, `username`, `created_at`). Зафиксировать единственного пользователя.

### A3. Выпуск JWT
- После успешной валидации — выпуск JWT (`System.IdentityModel.Tokens.Jwt`), подпись `Jwt:Secret` (= `JWT_SECRET`), claims: `sub` = внутренний `UserId`, `telegram_id`. Срок — разумный (например, 30 дней; продукт личный).
- Эндпоинты (`Bonds.Api/Endpoints/AuthEndpoints.cs`):
  - `POST /api/auth/telegram` — принимает payload виджета, возвращает `{ token, user }`.
  - `GET /api/auth/me` — по JWT возвращает текущего пользователя (для проверки сессии на фронте).

### A4. JWT middleware / защита эндпоинтов
- Настроить `AddAuthentication().AddJwtBearer(...)` с валидацией подписи/срока (или порт `JwtMiddleware` из cashpulse — выбрать один способ, предпочтительно стандартный `JwtBearer`).
- Все доменные эндпоинты — `.RequireAuthorization()`. Исключения: `GET /health`, `POST /api/auth/telegram`.
- Из JWT извлекать `UserId` и прокидывать в сервисы/репозитории (источник идентичности; больше никаких хардкод `UserId=1`).

### A5. Безопасность
- Bot token / JWT secret — только из ENV/секретов, не логировать.
- Ошибки авторизации не раскрывают деталей (одинаковый `401`/`403`).

## Часть B — Frontend

### B1. Экран логина
- Страница `/login`: компонент Telegram Login Widget (скрипт `telegram-widget.js`, `data-telegram-login = VITE_TELEGRAM_BOT_USERNAME`, `data-onauth` коллбэк).
- Коллбэк виджета → `POST /bonds/api/auth/telegram` → сохранить `token` в Zustand (persist) + localStorage.

### B2. Сессия и защита роутов
- `useAuthStore` (Zustand persist): `token`, `user`, `login()`, `logout()`.
- API-клиент добавляет заголовок `Authorization: Bearer <token>` ко всем запросам; на `401` — разлогинить и редирект на `/login`.
- `ProtectedRoute`/guard: при отсутствии валидной сессии — редирект на `/login`. На старте приложения — проверка `GET /bonds/api/auth/me`.
- Кнопка «Выйти» в шапке (в standalone-режиме; внутри shell — см. этап 09 про iframe-контекст).

## Часть C — Тесты

- Юнит (`Bonds.Tests`): валидация Telegram-подписи — корректный hash проходит, испорченный/просроченный отклоняется; чужой `id` → отказ; владелец → успех. (Порт `TelegramAuthServiceTests`.)
- Интеграционные (`Bonds.IntegrationTests`): защищённый эндпоинт без токена → `401`; с валидным токеном владельца → `200`; `JwtTestHelper` для генерации тестовых токенов.
- Frontend (Vitest + MSW): логин-флоу мокается, при 401 происходит logout/redirect.

---

## Критерии приёмки
- [ ] `POST /api/auth/telegram` с валидной подписью владельца → `200` + JWT; с невалидной подписью → `401`; с чужим `id` → `403`.
- [ ] Защищённый доменный эндпоинт без `Authorization` → `401`; с валидным токеном → `200`.
- [ ] `GET /api/auth/me` по токену возвращает владельца.
- [ ] Bot token и JWT secret нигде не логируются и не попадают на фронт.
- [ ] Фронт: без сессии любой защищённый роут редиректит на `/login`; после входа — доступ открыт; «Выйти» очищает сессию.
- [ ] Юнит- и интеграционные тесты авторизации зелёные.

## Проверка
```bash
dotnet test --filter "FullyQualifiedName~Auth"
# ручной smoke (локально):
curl -i -X POST localhost:5001/api/auth/telegram -H 'Content-Type: application/json' -d '<невалидный payload>'   # ожидаем 401
curl -i localhost:5001/api/positions    # без токена → 401 (после появления эндпоинтов)
( cd bonds-web && yarn test:run )
```

## Definition of Done
Вход только для владельца через Telegram, JWT защищает все доменные эндпоинты, фронт корректно ведёт сессию и обрабатывает 401. Тесты зелёные, секреты не утекают. Идентичность (`UserId`) берётся из JWT и используется дальше во всех модулях.

### Дальше → [`03-domain-model-and-storage.md`](03-domain-model-and-storage.md)
