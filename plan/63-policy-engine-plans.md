# Задача 63 — Движок правил и планы сделок (dry-run, без исполнения)

> **Кому адресовано.** `bond-implementer`. Контекст: дизайн §4, §5 (lifecycle), §9.
>
> **Зависимости:** 61, 62. **Порядок:** после 62.
>
> **Рамки:**
> - Заявки к брокерам **не отправляются** — нигде, никак.
> - Статусы исполнения только в модели. Исполнитель — фаза 3.

---

## Модель плана (миграция `033`)

### Таблица `trade_plans`

| Колонка | Тип / значения |
|---|---|
| `id` | |
| `user_id` | |
| `rule` | `P1_SWEEP` \| `P2_INFLOW` \| `P3B_LIQ_TO_BONDS` \| `P4_REBALANCE` \| `P5_WITHDRAW` \| `P7_FREEDOM_TOPUP` \| `MANUAL` |
| `trigger` | VARCHAR(255) — причина, человеческим языком |
| `status` | `Draft` \| `PendingApproval` \| `AutoApproved` \| `Approved` \| `Rejected` \| `Expired` \| `Submitting` \| `Done` \| `Failed` |
| `dry_run` | TINYINT |
| `created_at` | |
| `expires_at` | |
| `decided_at` | |
| `decided_via` | `web` \| `telegram` \| `auto` |
| `summary_json` | итоги: деньги до/после, комиссия, налог |

### Таблица `trade_plan_lines`

| Колонка | Тип / значения |
|---|---|
| `plan_id` | |
| `line_no` | |
| `broker` | |
| `account_id` | |
| `instrument_key` | |
| `ticker` | |
| `side` | `Buy` \| `Sell` |
| `quantity_lots` | |
| `limit_price` | валюта цены за 1 бумагу; для облигаций — рубли за бумагу (чистая цена), конверсия в пункты — у исполнителя |
| `est_amount` | |
| `est_fee` | |
| `est_tax` | |
| `currency` | |
| `reason` | |

## Правила (Core/Policy, чистые функции `(PolicyInput) → TradePlanDraft?`)

Общее для всех правил:
- Цена — mid стакана, округлённый к шагу цены: покупка вниз, продажа вверх (как `M:\Projects\trade\tn\__main__.py::_resolve_limit`).
- Количество — целые лоты. Комиссия — через `ICommissionRateProvider` (T-Invest) или `FreedomFeeModel`.
- Торговые часы: Мосбиржа 10:00–18:40 МСК, СПБ для `SPBRU` по options. Правила вне часов не генерируют планы.

**P1 — парковка кэша.** Свободные рубли T-Invest (`cashRubOnly` − уже запланированные покупки) ≥ цены 1 лота
самого дешёвого члена корзины `liquidity` → покупка членов по `sub_weight`. Сторонние фонды в авто — только
если в журнале есть их сделка с известной комиссией (иначе пропуск с reason). Статус `AutoApproved`, если
выполнены все условия:
- корзина `auto_buy`;
- сумма ≤ 25 000 ₽ на заявку и ≤ 50 000 ₽ за день (Σ AutoApproved сегодня) — **лимиты из
  `ExecutionLimitsOptions` в appsettings/env, не из БД**;

иначе — `PendingApproval`.

**P2 — распределение пополнения.** Триггер — новый Deposit (задача 61) или `POST /api/plans/inflow {amount}`.
- Деньги идут в корзины по дефициту (`deficitToTarget`), пропорционально.
- Корзина `fixed` → её члены по под-весам.
- Корзина `engine` (облигации) → кандидаты `CashAllocationService.Allocate(source=recommended)` с фильтром
  надёжности Green и реальным лотом из `instrument_trading_meta`.
- Всё, что не облигации, ставится как в P1. Облигационная часть плана — всегда `PendingApproval`.

**P3b — перелив ликвидности в облигации.** Раз в неделю (понедельник, после синка). Срабатывает, если корзина
`liquidity` в состоянии `HardAbove` или `Above` при weight > 0.20.
- Излишек до цели.
- Кандидаты — как в P2, плюс условия:
  - after-tax YTM к оферте/погашению ≥ `FundOutlookService` after-tax ожидаемой доходности фонда + **0.015** (ДОЛЯ, опция);
  - Green;
  - ликвидность не Caution;
  - не флоатер;
  - лимит эмитента.
- Нет кандидатов → плана нет, запись в лог: «замены нет».
- Всегда `PendingApproval`.

**P4 — ребаланс.** Только при `HardAbove/HardBelow` корзин без `hold`.
- Продажа излишка в пределах `auto_sell`.
- Покупка дефицита.
- Всегда `PendingApproval`.

**P5 — вывод.** `POST /api/plans/withdraw {amount | fraction, portfolio}`.
- Продажи по `liquidity_tier` 0 → 1 → 2 (`hold`/temporary — только если явно указаны в запросе `includeBaskets`).
- Внутри тира — по возрастанию оценки налога `SaleTaxEstimator` (сначала убыточные).
- Добирать до суммы с учётом комиссии и налога.
- В summary: «к выводу после налога и комиссии».
- Для Freedom — предупреждение о минимальной комиссии вывода $20.
- Всегда `PendingApproval`.

**Выход из временной корзины.** `POST /api/plans/exit-basket {code}` — продажа всех членов, всегда `PendingApproval`.

**P7 — доливка Freedom.** Есть USD-деньги ≥ $50 на Freedom → покупка `global_core`:
- до $315 — `SPYM.US` (ITS funds 0.5%);
- иначе — `VUAA.EU` (`FreedomFeeModel.CheaperRoute`).

Всегда `PendingApproval`. Freedom авто = 0.

## Планировщик и API

`PolicyRunner` — шаг `SyncCycleService` после синка всех брокеров:
1. Прогон правил P1, P2 (по новым Deposit), P7, P3b (понедельник), P4.
2. Не создаёт дубликат, если открыт план того же `rule` и `trigger`.
3. Истекает старые `PendingApproval` через 30 минут после создания (опция) или к закрытию сессии.
4. **`dry_run` = `ExecutionOptions.DryRun` (дефолт true)** — на этой задаче всегда true.

API (`PlansEndpoints`):

| Метод | Назначение |
|---|---|
| `GET /api/plans?status` | список планов |
| `GET /api/plans/{id}` | один план |
| `POST /api/plans/{id}/approve` | подтвердить |
| `POST /api/plans/{id}/reject` | отклонить |
| `POST /api/plans/inflow` | распределить пополнение |
| `POST /api/plans/withdraw` | вывод |
| `POST /api/plans/exit-basket` | выход из временной корзины |
| `POST /api/plans/preview` | `{rule, params}` — построить без сохранения |

`approve` в dry-run → статус `Approved` и пометка «не исполнен: dry-run».

## Тесты

Юнит на каждое правило, эталоны руками:
- P1 — 7 340 ₽, TMON@ 166.58 / TSAV по под-весу 0.5/0.5 → лоты, остаток.
- Лимит 25 000 ₽ на заявку переводит план в `PendingApproval`.
- P5 — порядок по налогу; добор с комиссией.
- P3b — фильтр по премии 1.5 п.п.
- P7 — граница $315.
- Округление цены к шагу (покупка вниз, продажа вверх).
- Вне торговых часов — нет плана.

Интеграционные:
- `inflow` / `withdraw` / `preview`, approve/reject переходы, повторный прогон без дубликатов.

## Фронт

Страница **«Планы»** (`/plans`):
- карточки планов: правило, причина, строки, итоги, статус;
- кнопки «Подтвердить / Отклонить»;
- баннер **«Режим наблюдения: сделки не отправляются»**.

На «Капитале» — кнопки «Распределить сумму…», «Вывести…», «Выйти из временной корзины».

Vitest.

## Критерии приёмки

- [ ] На проде после синка в «Планах» появляются корректные dry-run планы. Проверить на реальных данных:
  что предложит P1 и P5 на 10%. Приложить числа в отчёт.
- [ ] В коде нет ни одного вызова отправки заявки.
- [ ] pre-push-check зелёный.
