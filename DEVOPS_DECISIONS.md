# DevOps Decisions — Bond Portfolio Analytics (bonds)

> Деплой-канал описан и реализован как код (Dockerfile, docker-compose, GitHub Actions workflow-файлы), но **не выполнялся** в этих запусках — нет подключённого GitHub-репозитория, нет SSH-доступа к VDS, секреты не заведены. Это решение владельца (этап 01), не пробел реализации. Точный список того, что нужно для первого реального деплоя — в README, раздел «Деплой — TODO», и в финальной сводке по этапу 10.

## Архитектура деплоя

### Бэкенд (VDS, через GHCR — НЕ SCP, в отличие от cashpulse)
- Docker-образ собирается в CI, пушится в **GitHub Container Registry** (`ghcr.io/max31000/bonds-api`), на VDS — `docker pull` + `docker run`.
- Причина отличия от cashpulse (там SCP tar.gz, нет registry): план `00-overview-and-architecture.md` §3 явно фиксирует GHCR как целевую инфраструктуру для bonds; имитировать паттерн cashpulse 1:1 здесь не требовалось.
- Чтобы `docker pull` на VDS работал **без логина** (как у cashpulse — там просто другой механизм, но тот же принцип «не хранить креды на VDS»): пакет `bonds-api` в GHCR должен быть переключён в **Public** после первого успешного пуша (`Packages → bonds-api → Package settings → Change visibility → Public`). Альтернатива — `docker login ghcr.io` с PAT на VDS, если приватность пакета важнее.
- Контейнер `bonds-api` подключается к docker-сети общего MySQL-контейнера (та же БД, что у cashpulse, отдельная база `bonds` + отдельный пользователь) — **точное имя сети нужно сверить по факту на живом сервере** (`docker ps --format '{{.Names}}\t{{.Networks}}'`), placeholder `<DB_NETWORK>` в `backend.yml` оставлен намеренно до этой проверки.
- Деплой-скрипт оперирует **только** контейнером `bonds-api` (`docker stop/rm/run bonds-api`) — не трогает `cashpulse-api`, VPN, `credit_calc` и другие контейнеры на VDS.

### Фронтенд (VDS, статика через SCP — не GitHub Pages, в отличие от cashpulse)
- Vite-сборка (`base: '/bonds/'`) копируется через `appleboy/scp-action` в `/var/www/bonds/` на VDS; nginx раздаёт по `location /bonds/`.
- Причина отличия от cashpulse (там GitHub Pages): bonds встраивается в `portal-shell` на том же домене `mvv42.ru/bonds/`, отдельный домен GitHub Pages для этого не подходит — нужен путь под общим nginx.
- `VITE_API_BASE` (переменная, не секрет) — по умолчанию `/bonds/api`, относительный путь; работает и standalone, и в iframe portal-shell без пересборки под разные окружения.

## Изоляция от соседних сервисов (cashpulse, credit_calc, portal-shell)

| Ресурс | bonds | Соседи (не трогать) |
|---|---|---|
| Порт API | `5001` | cashpulse `5000`, credit_calc `8081` |
| Контейнер | `bonds-api` | `cashpulse-api` и др. |
| БД | `bonds` (своя база + пользователь на общем MySQL-сервере) | `cashpulse` |
| nginx location | `/bonds/`, `/bonds/api/`, `/bonds/health` | `/cashpulse/`, `/credit_calc/`, `/api/`, `location = /` |
| Локальный dev-порт MySQL (docker-compose) | `3307` (host) | cashpulse занимает `3306` |
| Локальный dev-порт фронта (`yarn dev`) | `5174` | cashpulse-web `5173` |

## Ключевые решения

| Решение | Причина |
|---|---|
| GHCR вместо SCP tar.gz для образа бэкенда | Зафиксировано в плане 00 как целевая инфраструктура bonds, отличается от cashpulse намеренно |
| Публичный пакет GHCR (после первого пуша) | `docker pull` на VDS без логина/PAT, как у cashpulse для своего механизма |
| `--network <DB_NETWORK>` placeholder, не хардкод | Имя сети — факт живого сервера, план явно требует сверки по SSH, а не предположения |
| Деплой-скрипт трогает только `bonds-api` | Изоляция — на VDS уже живут cashpulse/credit_calc/portal-shell, падать им нельзя |
| `.idea/` добавлен в `.gitignore` | Случайно закоммичен ревью-агентом, исправлено отдельным коммитом (см. историю git) |
| Локальные dev-порты (3307/5174) отличаются от прод-портов | Чтобы можно было поднять bonds и cashpulse одновременно на одной машине разработчика |

## GitHub Secrets/Variables — что нужно настроить (когда репозиторий будет создан)

Полный список — `plan/00-overview-and-architecture.md` §7. Кратко:

| Secret/Variable | Тип | Назначение | Готово сейчас? |
|---|---|---|---|
| `VDS_HOST`, `VDS_USER` | Secret | `89.167.34.3` / `root` | Известны, не секретны сами по себе |
| `VDS_SSH_KEY` | Secret | Приватный ключ деплоя | **Нет** — нужно сгенерировать пару и добавить публичный ключ на VDS |
| `DB_CONNECTION_STRING` | Secret | Строка подключения к базе `bonds` | **Нет** — зависит от шага создания БД/пользователя на живом сервере (план 01, C2) |
| `TINVEST_TOKEN` | Secret | Read-only токен T-Invest | **Нет** — выпускается владельцем в личном кабинете Т-Инвестиций |
| `JWT_SECRET` | Secret | Подпись JWT | Можно сгенерировать сразу (`openssl rand -base64 32`), не зависит от инфраструктуры |
| `TELEGRAM_BOT_TOKEN` | Secret | Тот же бот, что у cashpulse | **Нужно уточнить у владельца** — переиспользовать существующий бот или новый |
| `TELEGRAM_CHAT_ID` | Secret | Алерты CI о падении деплоя | **Нет** — у владельца |
| `OWNER_TELEGRAM_ID` | Secret | Allowlist логина (этап 02) | **Нет** — Telegram user id владельца |
| `VITE_API_BASE` | Variable | `/bonds/api` (дефолт уже корректен) | Готово, можно не трогать |
| `TELEGRAM_BOT_USERNAME` | Variable | Имя бота для Login Widget | Зависит от решения по боту выше |

### Генерация SSH-ключа для CI/CD (когда будет доступ к VDS)

```bash
ssh-keygen -t ed25519 -C "github-actions-bonds" -f ~/.ssh/bonds_deploy -N ""
ssh-copy-id -i ~/.ssh/bonds_deploy.pub root@89.167.34.3   # требует пароль/существующий доступ
# приватный ключ → GitHub Secret VDS_SSH_KEY
# публичный ключ остаётся в authorized_keys на VDS
```

## Первоначальный деплой (полный чеклист — см. README «Деплой — TODO» и план этапа 10)

```bash
# 1. Создать репозиторий на GitHub, push текущей истории main
# 2. SSH на VDS — сверить имя/сеть MySQL-контейнера, создать БД bonds + пользователя
# 3. Добавить nginx-локации /bonds/*, перезагрузить nginx
# 4. Добавить запись bonds в portal-shell/registry.json (отдельный репозиторий)
# 5. Сгенерировать SSH-ключ деплоя, добавить публичный ключ на VDS
# 6. Заполнить все GitHub Secrets/Variables из таблицы выше
# 7. Push в main → оба workflow (backend.yml/frontend.yml) запускаются автоматически
# 8. После первого пуша образа — сделать пакет bonds-api в GHCR публичным
```

## Защита существующих сервисов

Deploy-скрипт `backend.yml` использует только:
- `docker stop bonds-api` / `docker rm bonds-api` — только свой контейнер.
- `docker run --name bonds-api -p 127.0.0.1:5001:5001` — свой порт, не пересекается с `5000`/`8081`.

Контейнеры `cashpulse-api`, MySQL cashpulse, credit_calc, portal-shell — **не затрагиваются** ни одним шагом деплоя bonds.
