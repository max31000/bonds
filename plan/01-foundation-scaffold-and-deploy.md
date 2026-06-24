# Этап 01 — Фундамент: скаффолдинг монорепо, CI и деплой на VDS

> **Зависит от:** [`00-overview-and-architecture.md`](00-overview-and-architecture.md).
> **Эталон для копирования паттернов:** репозиторий `cashpulse` (структура `src/`, Dockerfile, воркфлоу, `MigrationRunner`) и `portal-shell` (`registry.json`, nginx-роутинг, basename).

## Цель этапа

Получить **«walking skeleton»**: пустой, но реально задеплоенный сервис. К концу этапа:
- монорепо со скелетом .NET-решения и Vite-фронта собирается локально;
- CI собирает и публикует образ в GHCR и фронт на VDS;
- на проде работает `https://mvv42.ru/bonds/health` (200) и `https://mvv42.ru/bonds/` (заглушка-страница);
- сервис `bonds` виден в portal-shell.

Доменной логики на этом этапе **нет** — только инфраструктура и сквозной деплой-канал.

---

## Часть A — Скаффолдинг монорепо

### A1. Структура репозитория
Создать репозиторий `bonds` (монорепо) со структурой, зеркалящей cashpulse:

```
bonds/
├── Bonds.sln
├── src/
│   ├── Bonds.Api/              # minimal API, Program.cs, Dockerfile, Endpoints/, Middleware/
│   ├── Bonds.Core/             # модели, интерфейсы, доменные сервисы (чистая логика)
│   └── Bonds.Infrastructure/   # Dapper-репозитории, MigrationRunner, DI, Migrations/*.sql, Connectors/, Scheduling/
├── tests/
│   ├── Bonds.Tests/            # xUnit, юнит-тесты (математика)
│   └── Bonds.IntegrationTests/ # xUnit, TestWebApplicationFactory, DatabaseFixture
├── bonds-web/                  # Vite + React + TS фронт
├── docker-compose.yml          # локальный запуск (api + mysql)
├── .env.example
├── .github/workflows/
│   ├── backend.yml
│   └── frontend.yml
├── .editorconfig
├── .gitignore
└── README.md
```

### A2. .NET solution
- `net8.0`, `Nullable=enable`, `ImplicitUsings=enable` во всех проектах.
- Ссылки проектов: `Api → Core, Infrastructure`; `Infrastructure → Core`; тесты → соответствующие проекты.
- Пакеты (минимум на этом этапе):
  - `Bonds.Api`: `Swashbuckle.AspNetCore`, `Microsoft.AspNetCore.Authentication.JwtBearer` (8.0), `Dapper`, `MySqlConnector`.
  - `Bonds.Infrastructure`: `Dapper`, `MySqlConnector`, `Microsoft.Extensions.{DependencyInjection,Hosting,Logging,Configuration}.Abstractions`; `<EmbeddedResource Include="Migrations\*.sql" />`.
  - Тесты: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing` (для интеграционных).
- `Program.cs` (`Bonds.Api`):
  - Minimal API, Swagger включён в Development.
  - `GET /health` → `200 OK` (JSON `{ "status": "ok" }`). Без авторизации.
  - Регистрация `MigrationRunner` (запуск миграций на старте; на этом этапе папка `Migrations/` пустая или с проверочной миграцией создания таблицы `schema_version`).
  - Чтение строки подключения из `ConnectionStrings:DefaultConnection`.
  - `ASPNETCORE_URLS=http://+:5001` по умолчанию.
- `MigrationRunner` — порт паттерна cashpulse: читает `*.sql` из `EmbeddedResource`, применяет по порядку имени, ведёт таблицу применённых миграций. (Полные доменные миграции — этап 03.)

### A3. Dockerfile (`src/Bonds.Api/Dockerfile`)
Многостадийный, как в cashpulse, но порт **5001**:
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5001
ENV ASPNETCORE_URLS=http://+:5001
# ... build/publish стадии по образцу cashpulse ...
ENTRYPOINT ["dotnet", "Bonds.Api.dll"]
```
Контекст сборки — корень репозитория.

### A4. docker-compose.yml (локальная разработка)
- Сервис `mysql` (mysql:8.0, база `bonds`) + сервис `api` (порт 5001). По образцу cashpulse. Используется только локально; на VDS БД общая (см. часть C).

### A5. Frontend skeleton (`bonds-web/`)
- Vite + React 19 + TS strict. `vite.config.ts`: `base: '/bonds/'`.
- React Router 7 с `basename={import.meta.env.BASE_URL}` (см. `portal-shell/docs/service-routing-integration.md`).
- Mantine 9 провайдер, тема (порт `theme.ts` из cashpulse как старт).
- Одна заглушка-страница: «Bond Portfolio Analytics — скоро» + дисклеймер.
- API-клиент `src/api/client.ts`: базовый путь из `import.meta.env.VITE_API_BASE ?? '/bonds/api'`.
- `scripts`: `dev`, `build` (`tsc -b && vite build`), `typecheck`, `lint`, `test`, `test:run`.
- yarn (`.yarnrc.yml`: `nodeLinker: node-modules`).

---

## Часть B — CI/CD (GitHub Actions)

Зеркалировать воркфлоу cashpulse, заменив имена/порт/пути.

### B1. `backend.yml`
- Триггер: push в `main` по путям `src/**`, `tests/**`, `Bonds.sln`, `.github/workflows/backend.yml`.
- Шаги: setup .NET 8 → restore → build (Release) → `dotnet test` (юнит) → `dotnet test` (интеграционные) → login GHCR → `docker/build-push-action` (теги `:latest` и `:sha-<sha>`) → деплой через `appleboy/ssh-action`.
- Деплой-скрипт на VDS (через ssh-action), **оперирует только `bonds-api`**:
  ```bash
  set -e
  docker pull ghcr.io/max31000/bonds-api:latest
  docker stop bonds-api && docker rm bonds-api || true
  docker run -d \
    --name bonds-api \
    --network <DB_NETWORK> \         # сеть, где живёт общий MySQL — сверить на сервере (часть C)
    --restart unless-stopped \
    -p 127.0.0.1:5001:5001 \
    -e "ConnectionStrings__DefaultConnection=${DB_CONNECTION_STRING}" \
    -e "Jwt__Secret=${JWT_SECRET}" \
    -e "Telegram__BotToken=${TELEGRAM_BOT_TOKEN}" \
    -e "Telegram__OwnerId=${OWNER_TELEGRAM_ID}" \
    -e "TInvest__Token=${TINVEST_TOKEN}" \
    ghcr.io/max31000/bonds-api:latest
  sleep 3
  curl -sf http://localhost:5001/health || (echo "Health check failed!" && exit 1)
  docker image prune -f
  ```
- Алерт в Telegram при `failure()` (как в cashpulse).

> **GHCR-доступ для pull на VDS (как в cashpulse).** В cashpulse `docker login ghcr.io` есть только в build-джобе (для push), а на VDS деплой делает голый `docker pull` без логина — это работает, потому что **пакет образа в GHCR публичный**. Сделать так же: после первого пуша образа открыть в GitHub `Packages → bonds-api → Package settings → Change visibility → Public`. Тогда на VDS логин не нужен. (Альтернатива, если оставлять приватным: добавить на VDS шаг `docker login ghcr.io` с PAT — но по умолчанию идём путём cashpulse: публичный пакет.)

### B2. `frontend.yml`
- Триггер: push по `bonds-web/**`, `.github/workflows/frontend.yml`.
- Шаги: setup Node 20 (cache yarn) → `yarn install --frozen-lockfile` → `yarn typecheck` → `yarn test:run` → `yarn build` (env `VITE_API_BASE`, `VITE_TELEGRAM_BOT_USERNAME`) → `appleboy/scp-action` `source: bonds-web/dist/*`, `target: /var/www/bonds/`, `strip_components: 2`, `rm: true` → verify `curl -sf https://mvv42.ru/bonds/`.
- Алерт в Telegram при падении.

### B3. Список секретов/переменных в README
Описать в `README.md` все GitHub Secrets/Variables из §7 этапа 00 (без значений). Для каждого — назначение и где взять.

---

## Часть C — Инфраструктура VDS (живой сервер)

> ⚠️ Для этой части нужен SSH-доступ. **Агент обязан запросить у пользователя SSH-пароль к VDS** (пользователь `root`, хост `89.167.34.3` / `mvv42.ru`). Пароль НЕ сохранять в файлах/коде/истории — использовать только в рамках интерактивной сессии. Если доступа нет — оформить часть C как ручную инструкцию пользователю и приостановить деплой-проверку.

### C1. Сверка реального состояния (обязательно перед изменениями)
Подключиться по SSH и установить факты:
```bash
# имя/сеть/порт MySQL-контейнера
docker ps --format '{{.Names}}\t{{.Networks}}\t{{.Ports}}' | grep -i mysql
# текущий nginx-конфиг портала
cat /etc/nginx/sites-enabled/portal   # или sites-enabled/* / conf.d/*
# занятые порты
ss -tlnp | grep -E ':500[0-9]'
```
Зафиксировать: точное имя MySQL-контейнера, имя его docker-сети, опубликован ли порт 3306 на хост. Это определяет `<DB_NETWORK>` в деплой-скрипте и хост в строке подключения.

### C2. Общий MySQL — отдельная БД и пользователь
Создать базу и пользователя с грантами только на неё (значение пароля сгенерировать и **передать пользователю** для занесения в `DB_CONNECTION_STRING`, в доки не писать):
```sql
CREATE DATABASE IF NOT EXISTS bonds CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'bonds_app'@'%' IDENTIFIED BY '<СГЕНЕРИРОВАННЫЙ_ПАРОЛЬ>';
GRANT ALL PRIVILEGES ON bonds.* TO 'bonds_app'@'%';
FLUSH PRIVILEGES;
```
Строка подключения (в GitHub Secret `DB_CONNECTION_STRING`), хост = имя MySQL-контейнера, если `bonds-api` в той же docker-сети:
```
Server=<имя_mysql_контейнера>;Port=3306;Database=bonds;User=bonds_app;Password=<пароль>;
```
Поэтому в деплой-скрипте (B1) `--network <DB_NETWORK>` = сеть MySQL-контейнера (из C1). Если по политике изоляции нежелательно цеплять `bonds-api` к чужой сети — альтернатива: подключиться к опубликованному `127.0.0.1:3306` через `--add-host host.docker.internal:host-gateway`. Выбор зафиксировать по факту C1.

### C3. nginx — добавить локации для bonds
В существующий server-блок `mvv42.ru` (443) добавить, **не трогая** `/cashpulse/`, `/credit_calc/`, `/api/`, `location = /`:
```nginx
    # bonds SPA — статика из /var/www/bonds (деплой через SCP)
    location /bonds/ {
        root /var/www;
        try_files $uri $uri/ /bonds/index.html;
        expires -1;
        add_header Cache-Control "no-store";
    }
    location ~* ^/bonds/assets/.*\.(js|css)$ {
        root /var/www;
        expires 7d;
        add_header Cache-Control "public";
    }
    location ~* ^/bonds/.*\.(png|jpg|jpeg|gif|ico|svg|woff|woff2)$ {
        root /var/www;
        expires 30d;
        add_header Cache-Control "public";
    }
    location = /bonds { return 301 /bonds/; }

    # bonds API — прокси на контейнер :5001, префикс /bonds/api/ → /api/ внутри приложения
    location /bonds/api/ {
        proxy_pass http://127.0.0.1:5001/api/;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 120s;
    }
    location = /bonds/health {
        proxy_pass http://127.0.0.1:5001/health;
        proxy_set_header Host $host;
    }
```
> **Важно про префикс.** nginx срезает `/bonds/api/` → проксирует на `…:5001/api/`. Значит маршруты в приложении остаются `/api/...` (как в cashpulse), а фронт ходит на `/bonds/api/...`. Альтернатива — `UsePathBase("/bonds")` в ASP.NET; **выбран вариант с rewrite в nginx**, не использовать PathBase, чтобы Swagger/маршруты были простыми.

Применить: `nginx -t && systemctl reload nginx`. Создать каталог `mkdir -p /var/www/bonds`.

### C4. portal-shell registry.json
В репозитории `portal-shell` добавить в `registry.json` запись (отдельный PR/commit в `portal-shell`):
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
После мерджа в `main` portal-shell задеплоится сам (его воркфлоу).

---

## Критерии приёмки (агент обязан подтвердить каждый пункт)

- [ ] `dotnet build Bonds.sln -c Release` — успешно.
- [ ] `dotnet test` — проходит (на этом этапе тесты могут быть минимальными/smoke).
- [ ] Локально через docker-compose: `curl -s localhost:5001/health` → `{"status":"ok"}`.
- [ ] `cd bonds-web && yarn typecheck && yarn build` — успешно; в `dist` ассеты с префиксом `/bonds/`.
- [ ] Воркфлоу `backend.yml` и `frontend.yml` присутствуют и синтаксически валидны (`actionlint` или ручная проверка).
- [ ] (Прод, при наличии SSH) `curl -sf https://mvv42.ru/bonds/health` → 200.
- [ ] (Прод) `https://mvv42.ru/bonds/` отдаёт заглушку без ошибок в консоли; ассеты грузятся с `/bonds/assets/...`.
- [ ] (Прод) `https://mvv42.ru/?app=bonds` показывает сервис в iframe portal-shell.
- [ ] Существующие сервисы (`/cashpulse/`, `/credit_calc/`, `/`) продолжают работать (проверить curl-ом).
- [ ] Нигде не закоммичены секреты; пароль MySQL/токены переданы пользователю отдельно.

## Проверка (команды)
```bash
dotnet build Bonds.sln -c Release
dotnet test
docker compose up -d --build && sleep 5 && curl -s localhost:5001/health
( cd bonds-web && yarn typecheck && yarn build )
# прод (после деплоя):
curl -sf https://mvv42.ru/bonds/health && echo OK
curl -sf https://mvv42.ru/cashpulse/ >/dev/null && echo "cashpulse intact"
```

## Definition of Done
Все критерии приёмки отмечены с приложенным выводом команд. Деплой-канал сквозной: пуш в `main` → образ в GHCR → контейнер `bonds-api` на :5001 → `/bonds/health` зелёный; пуш фронта → `/var/www/bonds/` → `/bonds/` открывается и виден в портале. Изоляция соблюдена (свой порт, своя БД/пользователь, свой контейнер, чужие сервисы целы).

### Дальше → [`02-telegram-auth.md`](02-telegram-auth.md)
