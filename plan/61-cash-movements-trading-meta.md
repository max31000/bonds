# Задача 61 — Движение денег T-Invest и торговые метаданные инструментов

> **Кому адресовано.** `bond-implementer`. Контекст: CLAUDE.md, docs/CODEBASE-GUIDE.md, дизайн docs/2026-10-23-capital-automation-design.md §2.4, §4 (P1/P2).
>
> **Зависимости:** 58–60. **Порядок:** первая в фазе 2 — ей пользуются 62–64.
>
> **Рамки:** только данные и чтение. Ни правил, ни заявок.

---

## Проблема

Правилам фазы 2 нужны три вещи, которых нет.

1. **Факт пополнения или вывода.** `BondSyncService` выбрасывает `OPERATION_TYPE_INPUT/OUTPUT`
   (`BondSyncService.cs:~510`), поэтому триггер «пришли деньги → распределить» не на чем построить.
2. **Сколько свободных рублей на счёте.** Сейчас `accounts.cash_rub` включает валюту — см. CODEBASE-GUIDE: `cashRubOnly`.
3. **Можно ли и как торговать бумагой.** Нужны лот, шаг цены, `apiTradeAvailableFlag`, `buy/sellAvailableFlag`, класс
   торгов и признак выплат у фонда. Сейчас лот захардкожен в 1 (`CashAllocationService`, `LotSizeIsAssumed`).

## Что сделать

### A. Backend

1. **Миграция `031`.**

   **`account_cash_movements`** — по образцу `broker_operations` из задачи 60, но для T-Invest:

   | Колонка | Тип | Комментарий |
   |---|---|---|
   | `account_id` | | |
   | `external_id` | | |
   | `op_date` | | |
   | `type` | `Deposit\|Withdrawal` | |
   | `amount_rub` | DECIMAL(20,4) | со знаком брокера |
   | `currency` | CHAR(3) | |
   | `amount` | DECIMAL(20,4) | в своей валюте |

   Ключ: UNIQUE (account_id, external_id).

   **`instrument_trading_meta`**:

   | Колонка | Тип |
   |---|---|
   | `broker` | |
   | `instrument_key` | ISIN, иначе тикер |
   | `uid` | VARCHAR(64) NULL — T-Invest instrument uid |
   | `figi` | |
   | `ticker` | |
   | `class_code` | |
   | `lot` | DECIMAL(20,6) |
   | `min_price_increment` | DECIMAL(20,9) — в валюте цены за 1 бумагу; для облигаций T-Invest — в ПУНКТАХ, сверить с `GetLastPrices` и задокументировать |
   | `currency` | |
   | `api_trade_available` | |
   | `buy_available` | |
   | `sell_available` | |
   | `pays_distributions` | TINYINT NULL — для фондов: были ли выплаты за 12 мес. |
   | `updated_at` | |

   Ключ: UNIQUE (broker, instrument_key).

2. **Синк T-Invest.** INPUT/OUTPUT → `account_cash_movements`; их кладёт тот же проход журнала, что сейчас их выбрасывает.

3. **`TradingMetaRefresher`** — шаг `SyncCycleService` раз в сутки.

   T-Invest, по всем инструментам из позиций и из белого списка «вселенной фондов» (опция `TradingMetaOptions.FundUniverse`, дефолт):
   - Т-Капитал: `TMON@`, `TSAV`, `TBRU@`, `TOFZ@`, `TPAY`;
   - сторонние: `LQDT` (класс SPBRU), `SBFR`, `SBRB`, `OBLG`, `SBGB`, `AKMB`.

   Откуда данные:
   - лот, шаг цены и флаги — из `InstrumentsService` (`BondBy` / `EtfBy` / `FindInstrument`);
   - выплаты — из `GetDividends` за 365 дней.

   У тикера бывает несколько `classCode`. Берём тот, где `apiTradeAvailableFlag = true`, иначе TQBR. Правило и пример
   с LQDT (`TQBR`: api=false, `SPBRU`: api=true) — в doc-comment.

   Freedom: тикеры позиций плюс `SPYM.US`, `VUAA.EU`. Лот — 1, шаг — `min_step` из котировок. Флаг ITS-двойника
   (`its_twin`) — котировка `<base>.ITS` существует, через `getStockQuotesJson`.

4. **API.**
   - `GET /api/cash` → по каждому брокеру: свободные деньги по валютам (рубли без валюты — `cashRubOnly`), последние 10
     пополнений и выводов.
   - `GET /api/trading-meta?broker&keys`.

5. **Тесты.**
   - Маппинг INPUT/OUTPUT с идемпотентностью.
   - Выбор `classCode`.
   - Обновление метаданных с фейковым клиентом.
   - Единицы шага цены облигаций — эталон: шаг 0.01 пункта при номинале 1000 = 0.1 ₽.

### B. Frontend

На «Обзоре» — блок «Деньги на счетах» (по брокерам и валютам) и последние пополнения. Vitest.

## Критерии приёмки

- [ ] Пополнения и выводы T-Invest видны и не дублируются.
- [ ] Для всех позиций и фондов вселенной есть строка метаданных с корректным лотом и шагом цены. Проверить вручную на проде: TMON@ лот 1, шаг 0.01.
- [ ] pre-push-check зелёный.
