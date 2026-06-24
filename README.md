# Bond Portfolio Analytics (bonds)

Персональный аналитический сервис для облигационного портфеля: подтягивает позиции
и операции с брокерского счёта Т-Инвестиции, обогащает их облигационной математикой
(НКД, YTM, дюрация, выпуклость, PVBP, G-спред), строит персональный календарь
денежного потока с НДФЛ, считает XIRR портфеля и генерирует сигналы (оферты, купоны,
концентрация и т.д.).

Полная бизнес-спека: [`bond-portfolio-analytics-spec.md`](bond-portfolio-analytics-spec.md).
Технический план по этапам: [`plan/`](plan/) (начать с `plan/00-overview-and-architecture.md`).

> Все расчёты в этом сервисе — аналитические оценки, не инвестиционные рекомендации.

## Статус

**Этапы 01–06 выполнены** (фундамент/CI-конфигурация, Telegram-авторизация, доменная
модель и хранилище, внешние коннекторы MOEX/T-Invest, расчётный движок, проекция
денежного потока и портфельная аналитика). Этап 07 (сигналы и планировщик) — следующий.

- **Этап 01** — скаффолдинг монорепо, CI-воркфлоу (`backend.yml`/`frontend.yml`),
  `/health`, заглушка-страница на фронте. Деплой на живой VDS в рамках этого этапа
  **не выполнялся** — см. раздел "Деплой — TODO" ниже (он остаётся актуальным и после
  всех последующих этапов: ни один из них не требовал живого VDS/GitHub-репозитория).
- **Этап 02** — Telegram Login + JWT: `POST /api/auth/telegram`, `GET /api/auth/me`,
  allowlist по `Telegram:OwnerId`, `FallbackPolicy.RequireAuthenticatedUser()` на всех
  доменных эндпоинтах, фронтовый `useAuthStore`/`ProtectedRoute`.
  Тесты — `Bonds.Tests/TelegramAuthServiceTests.cs`, `Bonds.IntegrationTests/AuthEndpointsTests.cs`.
- **Этап 03** — доменные модели (§5 спеки) в `Bonds.Core/Models`, миграции
  `003_domain_schema.sql`/`004_add_user_base_currency.sql`, Dapper-репозитории на каждый
  агрегат, идемпотентный upsert операций по `ExternalId`, пометки "неполные данные"/
  "не-RUB вне скоупа". 38 интеграционных round-trip тестов в `Bonds.IntegrationTests`.
- **Этап 04** — коннекторы `Bonds.Infrastructure/Connectors/Moex` (резолвер ISIN→SECID,
  парсеры купонов/амортизаций/оферт/Gcurve по реальным фикстурам MOEX ISS) и
  `Connectors/TInvest` (gRPC SDK `Tinkoff.InvestApi`, контракт по облигациям верифицирован
  отражением сборки — см. `Connectors/TInvest/README.md`), оркестратор `BondSyncService`.
- **Этап 05** — изолированный расчётный движок `Bonds.Core/Calculation` без I/O: НКД,
  грязная цена, YTM/доходность к оферте (Ньютон-Рафсон + фолбэк на бисекцию), дюрация
  Маколея/модифицированная, выпуклость, PVBP, G-спред по официальной методике MOEX
  (реконструкция NSS-кривой с гауссовыми корректирующими членами), XIRR. Полная
  обработка флоатеров/индексируемых/амортизации/оферт/неполных данных.
- **Этап 06** — `Bonds.Core/CashFlow` (проекция купонов/амортизаций/погашений с НДФЛ 13%
  на купонный доход, агрегация по месяцам/позициям) и `Bonds.Core/Analytics` (XIRR
  портфеля, композиция по эмитенту/сектору/типу купона/корзинам дюрации, сравнение и
  сортировка позиций с дисклеймером, анализ замены между текущими позициями).
- **Ревью после этапов 01–03 и 04–06** — два независимых прохода нашли и исправили
  реальные баги: некорректная передача enum-полей в Dapper-параметры на запись (этап
  03), неверный масштаб единиц измерения в G-спреде и рассинхронизация знака суммы
  операции между сервисами XIRR/InvestedRub (этапы 04-06). См. историю коммитов
  `Review: ...` для деталей.

Движок сигналов, доменные HTTP-эндпоинты и фронтовые дашборды — этапы 07+, ещё не реализованы.

## Архитектура

```
Backend (.NET 8 / ASP.NET Core Minimal API, Dapper + MySqlConnector)
  └── Bonds.Api             — HTTP endpoints, middleware, Program.cs
  └── Bonds.Core            — Domain models, чистая логика (Calculation Engine и т.д.)
  └── Bonds.Infrastructure  — Dapper-репозитории, MigrationRunner, коннекторы (MOEX/T-Invest)

Frontend (React 19 + TypeScript + Mantine 9)
  └── bonds-web/            — Vite app, base: '/bonds/'

Database: MySQL 8.0
Deploy (план): GitHub Actions → GHCR → VDS (Docker), фронт → /var/www/bonds/ через SCP
```

Маппинг модулей бизнес-спеки на этапы плана — см. `plan/00-overview-and-architecture.md`, §4.

## Локальный запуск

### Предварительные требования
- .NET 8 SDK
- Node.js 20+, Yarn 1.22.x (`nodeLinker: node-modules`)
- Docker + Docker Compose

### Backend + MySQL (docker-compose)

```bash
docker compose up -d --build
curl -s localhost:5001/health
# => {"status":"ok"}
```

### Backend локально без Docker (например, если Docker недоступен)

```bash
# Поднять только MySQL
docker compose up mysql -d

cd src/Bonds.Api
dotnet run
# API на http://localhost:5001, Swagger UI: http://localhost:5001/swagger
```

### Frontend

```bash
cd bonds-web
yarn install
yarn dev
# http://localhost:5174
```

В dev фронт ходит на `VITE_API_BASE` (по умолчанию `/bonds/api`, относительный путь —
работает и standalone через Vite proxy/прямой адрес, и внутри portal-shell iframe).

### Сборка и проверки

```bash
dotnet build Bonds.sln -c Release
dotnet test Bonds.sln -c Release
( cd bonds-web && yarn typecheck && yarn test:run && yarn build )
```

## Структура репозитория

```
BondAnalytics/
├── Bonds.sln
├── src/
│   ├── Bonds.Api/              — minimal API, Program.cs, Dockerfile, Endpoints/, Middleware/
│   ├── Bonds.Core/             — модели, интерфейсы, доменные сервисы (чистая логика)
│   └── Bonds.Infrastructure/   — Dapper-репозитории, MigrationRunner, DI, Migrations/*.sql, Connectors/
├── tests/
│   ├── Bonds.Tests/            — xUnit, юнит-тесты
│   └── Bonds.IntegrationTests/ — xUnit, TestWebApplicationFactory + Testcontainers MySQL
├── bonds-web/                  — Vite + React + TS фронт
├── docker-compose.yml          — локальный запуск (api + mysql)
├── .env.example
├── .github/workflows/
│   ├── backend.yml
│   └── frontend.yml
├── .editorconfig
├── .gitignore
├── bond-portfolio-analytics-spec.md
├── plan/                       — технический план по этапам (00…10)
└── README.md
```

## Конвенции (см. `plan/00-overview-and-architecture.md`, §8)

- Namespace/сборки: `Bonds.Api`, `Bonds.Core`, `Bonds.Infrastructure`, `Bonds.Tests`, `Bonds.IntegrationTests`.
- Docker-образ: `ghcr.io/max31000/bonds-api`. Контейнер: `bonds-api`. БД: `bonds`. Порт: `5001`.
- Фронт-пакет: `bonds-web`. Базовый путь: `/bonds/`.
- Таблицы MySQL: snake_case, множественное число (`instruments`, `positions`, `operations`, ...).
- Эндпоинты внутри приложения — `/api/...`; nginx навешивает префикс `/bonds` на проде.

## Секреты (GitHub Actions Secrets/Variables)

Полный список из `plan/00-overview-and-architecture.md`, §7. **Значения не вписываются
в репозиторий** — задаются в `Settings → Secrets and variables → Actions` живого
GitHub-репозитория (когда он будет создан и подключён; в рамках этого запуска
репозиторий локальный, без удалённого GitHub).

| Имя | Тип | Назначение | Где взять |
|---|---|---|---|
| `VDS_HOST` | Secret | IP/домен VDS для деплоя (`89.167.34.3` / `mvv42.ru`) | известен владельцу |
| `VDS_USER` | Secret | SSH-пользователь VDS (`root`) | известен владельцу |
| `VDS_SSH_KEY` | Secret | Приватный SSH-ключ для деплоя | сгенерировать отдельную ключевую пару для CI, публичный ключ добавить на VDS |
| `DB_CONNECTION_STRING` | Secret | Строка подключения к базе `bonds` на общем MySQL VDS | создаётся вручную на VDS (см. "Деплой — TODO", шаг 2) |
| `TINVEST_TOKEN` | Secret | Read-only токен T-Invest API | личный кабинет Т-Инвестиций → выпуск токена с правами только на чтение |
| `JWT_SECRET` | Secret | Подпись JWT (≥32 символа, случайная строка) | сгенерировать (`openssl rand -base64 32`) |
| `TELEGRAM_BOT_TOKEN` | Secret | Токен Telegram-бота (тот же бот, что у cashpulse) | у владельца, из настроек существующего бота |
| `TELEGRAM_CHAT_ID` | Secret | Chat ID для алертов CI о падении деплоя | у владельца |
| `OWNER_TELEGRAM_ID` | Secret | Telegram user id владельца (allowlist авторизации, этап 02) | узнать у `@userinfobot` или аналога |
| `VITE_API_BASE` | Variable | Базовый путь API для фронта (по умолчанию `/bonds/api`) | задать как Variable, не Secret |
| `TELEGRAM_BOT_USERNAME` | Variable | Имя бота для Telegram Login Widget на фронте | задать как Variable, не Secret |

Локальная разработка использует dev-заглушки из `.env.example` /
`appsettings.Development.json` (например `Jwt:Secret = dev-secret-change-me-...`,
пароль MySQL `dev-only-not-for-prod`) — они **непригодны для прода** и существуют
только для локального docker-compose/`dotnet run`.

## CI/CD — текущий статус этого запуска

`.github/workflows/backend.yml` и `.github/workflows/frontend.yml` присутствуют,
зеркалируют пайплайны `cashpulse` (адаптированы под имена/порт `bonds`) и прошли
синтаксическую проверку `actionlint` (0 замечаний). **Эти воркфлоу не выполнялись** —
нет удалённого GitHub-репозитория, не логинились в GHCR, не пушили образы, не трогали
VDS. Когда репозиторий будет подключён к GitHub:

1. Открыть `Packages → bonds-api → Package settings → Change visibility → Public`
   после первого успешного пуша образа (см. план 01, B1 — позволяет `docker pull`
   на VDS без логина, как у cashpulse).
2. Заполнить секреты/переменные из таблицы выше.
3. Push в `main` запускает оба пайплайна.

## Деплой — TODO (часть C плана 01, не выполнена в этом запуске)

Эта часть требует SSH-доступа к живому VDS (`root@89.167.34.3` / `mvv42.ru`), которого
не было в текущем запуске. Чеклист ручных шагов для следующего запуска/агента:

1. **Сверить реальное состояние сервера** (план 01, C1):
   - `docker ps --format '{{.Names}}\t{{.Networks}}\t{{.Ports}}' | grep -i mysql` —
     зафиксировать точное имя контейнера MySQL и его docker-сеть (предполагается
     общий с cashpulse, но это нужно подтвердить фактом, не предполагать).
   - `cat /etc/nginx/sites-enabled/portal` (или `sites-enabled/*` / `conf.d/*`) —
     текущий nginx-конфиг.
   - `ss -tlnp | grep -E ':500[0-9]'` — убедиться, что порт `5001` свободен.
2. **Создать базу и пользователя MySQL** (план 01, C2):
   ```sql
   CREATE DATABASE IF NOT EXISTS bonds CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
   CREATE USER IF NOT EXISTS 'bonds_app'@'%' IDENTIFIED BY '<СГЕНЕРИРОВАННЫЙ_ПАРОЛЬ>';
   GRANT ALL PRIVILEGES ON bonds.* TO 'bonds_app'@'%';
   FLUSH PRIVILEGES;
   ```
   Пароль сгенерировать на месте, не записывать в репозиторий/доки — передать
   владельцу для занесения в секрет `DB_CONNECTION_STRING`.
3. **Подставить `<DB_NETWORK>`** в `.github/workflows/backend.yml` (строка
   `--network <DB_NETWORK>`) — именем реальной docker-сети MySQL-контейнера,
   определённым на шаге 1. Если изоляция нежелательна — альтернатива из плана
   (`host.docker.internal:host-gateway` на опубликованный `127.0.0.1:3306`).
4. **Добавить nginx-локации для bonds** (план 01, C3) в существующий server-блок
   `mvv42.ru` (443), не трогая `/cashpulse/`, `/credit_calc/`, `/api/`, `location = /`:
   - `location /bonds/` → статика `/var/www/bonds/`, SPA fallback на `index.html`.
   - `location /bonds/api/` → `proxy_pass http://127.0.0.1:5001/api/;` (префикс
     срезается nginx'ом, в приложении маршруты остаются `/api/...`).
   - `location = /bonds/health` → `proxy_pass http://127.0.0.1:5001/health;`.
   - Точный текст конфига — в `plan/01-foundation-scaffold-and-deploy.md`, раздел C3.
   - `mkdir -p /var/www/bonds`, затем `nginx -t && systemctl reload nginx`.
5. **Добавить запись `bonds` в `portal-shell/registry.json`** (план 01, C4) —
   отдельный коммит/PR в репозитории `portal-shell` (не в этом репозитории):
   ```json
   {
     "id": "bonds",
     "name": "Bond Analytics",
     "description": "Аналитика облигационного портфеля",
     "icon": "📈",
     "path": "/bonds/",
     "color": "#7048e8"
   }
   ```
6. После шагов 1–5 и подключения репозитория к GitHub — первый push в `main`
   должен задеплоить сквозной канал. Проверить:
   ```bash
   curl -sf https://mvv42.ru/bonds/health && echo OK
   curl -sf https://mvv42.ru/cashpulse/ >/dev/null && echo "cashpulse intact"
   ```

**Ни один из критериев приёмки плана 01, требующих прода, не проверялся и не
имитировался в этом запуске** — см. финальный отчёт агента, реализовавшего этап 01,
для точного статуса каждого пункта.

## MVP-ограничения (после этапа 06)

- Сигналов/триггеров (оферты, концентрация, дрейф дюрации) и планировщика автосинка
  нет — этап 07. Доменных HTTP-эндпоинтов и фронтовых дашборд сверх логина нет —
  этапы 08–09.
- T-Invest коннектор (этап 04) написан и протестирован против мока SDK — без реального
  read-only токена живые вызовы не выполнялись (см. `Connectors/TInvest/README.md`).
- Стакан (bid/ask) запрашивается у T-Invest, но не персистируется — осознанно вне MVP
  (спека §8 относит предупреждение о низкой ликвидности к категории "на будущее").
- Деплой на живой VDS не выполнялся ни на одном из этапов 01–06 — см. "Деплой — TODO".
  Нет подключённого GitHub-репозитория, секреты не заведены.
