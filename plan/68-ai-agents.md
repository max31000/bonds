# Задача 68 — ИИ-агенты: наблюдатель и исследователь (опционально, через подключение `claude_code`)

> **Кому адресовано.** `bond-implementer` (backend) + изменения инфраструктуры в `mvv42-infra` (отдельная задача у владельца, если нужен новый контейнер).
>
> **Зависимости:** 58 (подключения), 63 (планы), 64 (бот).
>
> **Рамки:**
> - Агент **не торгует** и не меняет лимиты или настройки.
> - Единственный пишущий инструмент — `propose_plan`. Он создаёт план всегда в статусе `PendingApproval`, `source=agent`.
> - Провайдер ИИ — интерфейс `IAgentRunner`, первая реализация — Claude Code CLI. OpenAI и другие добавляются реализацией этого интерфейса.

---

## Архитектура

1. **`IAgentRunner`.**
   - Вход: `RunAsync(AgentTask task, AgentCredentials creds, ct) → AgentResult(text, costUsd?, exitCode)`.
   - Реализация `ClaudeCodeAgentRunner`:
     - запускает процесс `claude -p "<prompt>" --output-format json --strict-mcp-config --mcp-config <file> --tools "" --allowedTools "mcp__bonds__*" --permission-prompts none --max-turns N`;
     - env процесса: **только** `CLAUDE_CODE_OAUTH_TOKEN=<токен пользователя>`, `HOME=<временный каталог>`, `PATH`. Переменные хоста не наследуются, `ANTHROPIC_API_KEY` удаляется явно;
     - `--bare` не использовать;
     - таймаут на запуск, kill дерева процессов.
   - Как поставить CLI:
     - вариант A — Node.js и `@anthropic-ai/claude-code` в образе `bonds-api` (проще, но образ толще);
     - вариант B — отдельный контейнер-воркер с HTTP API внутри `cashpulse-network`.
   - **Решение (оркестратор, 2026-10-24): вариант A** — Node.js LTS + закреплённая версия `@anthropic-ai/claude-code` в финальном образе `bonds-api`; вынести в B, если образ разрастётся.
2. **MCP-сервер bonds** (только чтение):
   - Встроенный HTTP MCP endpoint `/api/mcp`, доступный только из процесса агента: localhost и одноразовый bearer на один запуск.
   - Или stdio-сервер — отдельный бинарь в образе.
   - Инструменты:
     - `portfolio`, `baskets`, `plans`, `signals`, `cashflow_next_90d`, `bond_card(isin)`, `scorecard(isin)`, `freedom_holdings`, `cash`;
     - `propose_plan(kind=inflow|withdraw|exit_basket, amount|fraction|basketCode, rationale)`: валидация через `PolicyService.CreateManualAsync`, статус всегда `PendingApproval`, `source=agent`.
      Доступен **только** задачам `EventReview` и `OnDemand` (вид задачи хранится в bearer запуска); `DailyDigest` и `MonthlyReview` получают ошибку инструмента.
      Не больше `Agent:MaxProposalsPerRun` (1) планов за запуск — атомарная бронь слота, при ошибке валидации слот возвращается.
3. **Задачи агента** (`AgentTaskKind`) по расписанию и событиям:
   - `DailyDigest` (будни 19:00 МСК): что изменилось, события, планы — кратко в бот;
   - `EventReview`: новое Critical-событие (оферта, дефолт эмитента, просадка) — разбор и, если уместно, `propose_plan`;
   - `MonthlyReview` (1-е число): дрейф корзин, комиссии против модели, кандидаты P3b;
   - `OnDemand` (`/ask <вопрос>` в боте).
4. **Хранение.**
   - Таблица `agent_runs`: `user_id`, `kind`, `started`, `finished`, `exit_code`, `cost_usd`, `tokens`, `result_text`, `error`.
   - Лимиты из `AgentOptions`: запусков в сутки на пользователя, `max_turns`, таймаут.
5. **UI.**
   - Раздел «Агент»: история запусков, текст, стоимость.
   - Включение — при `capabilities.aiAgents`.
   - Модель: Sonnet для рутины, Opus для ежемесячного обзора; настраивается.

## Безопасность

- Токен пользователя расшифровывается только на время запуска и передаётся в env процесса. В аргументах командной строки его нет — их видно в `ps`.
- MCP только читает. `propose_plan` идёт тем же путём подтверждения, что и любой план.
- Ответ агента — недоверенный текст: в бот он уходит экранированным и не исполняется.

## Тесты

- Fake runner.
- Сборка env процесса: нет `ANTHROPIC_API_KEY`, нет переменных хоста.
- Таймаут и kill.
- `propose_plan` всегда `PendingApproval`.
- MCP-инструменты на тестовой БД.
- Интеграционный тест с реальным CLI — опционально, под env-флагом.

## Уточнения оркестратора

- **MCP.** HTTP-эндпоинт внутри API `/internal/mcp`, слушает только loopback (отдельный Kestrel endpoint `127.0.0.1:<port>` или проверка `RemoteIpAddress` loopback). Авторизация — одноразовый bearer-токен на запуск: случайные 32 байта, TTL = таймаут запуска, привязан к user_id. Отдельно от cookie-авторизации пользователей.
  - Можно взять официальный C# MCP SDK (`ModelContextProtocol.AspNetCore`), если он стабилен.
  - Иначе минимальная реализация JSON-RPC MCP (`initialize`, `tools/list`, `tools/call`) по спецификации streamable HTTP.
- **Токен пользователя** (`claude_code`) расшифровывается только на время запуска и попадает **только** в env дочернего процесса — не в argv и не в логи. В env нет `ANTHROPIC_API_KEY`; переменные хоста не наследуются, кроме `PATH`/`HOME` (временный каталог).
- **Расписание:**
  - `DailyDigest` по будням в 19:00 МСК — только пользователям с `capabilities.aiAgents` и `agent_enabled` (новая настройка, по умолчанию выключена);
  - `EventReview` — на новое Critical-событие, не чаще 3 раз в сутки;
  - `/ask` в боте.
- **Модель** (`claude-sonnet-...` / `opus`) задаётся в настройках агента. По умолчанию — алиас `sonnet` в CLI, без жёстких версий.
- **Без CLI в тестовом окружении:** `IAgentRunner` с fake. Есть опциональный тест с реальным CLI — под env `CLAUDE_CODE_OAUTH_TOKEN_TEST`, категория Sandbox.
- **UI:**
  - раздел «Агент»: включение, модель, лимит запусков в сутки, история запусков;
  - результат — текст: в вебе экранируется, в боте отправляется как HTML-escape.

## Ревью (2026-10-25) — исправления

- **Изоляция процесса:** CLI под пользователем `agent` (uid/gid 10001) через `setpriv` (`--clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all`), API пока root.
  Каталоги запусков — `/var/lib/bonds-agent` (root 0711; при старте создаётся и чистится от `run-*`), каталог запуска 0700 и MCP-конфиг 0600 — владелец 10001. Ключи DataProtection — 0700 root.
- **Dockerfile:** базовые образы по `@sha256`; в финальном образе — только нативный `claude` (без Node.js); сборка падает без нужных флагов в `claude --help`; PR-сборка образа без push в `deploy.yml`.
- **MCP:** кроме `RemoteIpAddress` — loopback и `LocalIpAddress`; заголовки прокси → 404.
- **Лимиты и очередь:** суточный лимит пользователя сжимается до `Agent:MaxDailyRunLimit`; не больше одного ждущего `/ask` на пользователя, события дописываются в ждущий разбор;
  отказ очереди не тратит паузу `/ask`; недоступный токен — запись запуска с ошибкой (расписание не повторяет каждую минуту).
- **AllowWebResearch** — по умолчанию выкл., не рекомендуется до отдельного контейнера-воркера; при включении — предупреждение в логе на старте.
