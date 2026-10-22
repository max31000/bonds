# Задача 55 — Валюта как актив: синк валютных позиций/операций, курс, журнал доходов, баланс

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (паттерны, контракт единиц, разделы «Фонды — отдельный домен» и «Доходы (задача 52)» — читать обязательно).
>
> **Зависимости:** 51–54 (домен фондов как образец; `IncomeLedgerBuilder`, `PositionIncomeCalculator`,
> `IncomeStatisticsService`, `/api/income`, `/api/portfolio/summary`).
> **Порядок в волне:** первая; 56 (UI) — строго после.
>
> **Рамки (что НЕ делать):** валюта — ОТДЕЛЬНЫЙ домен по образцу фондов (свои таблицы), не трогать
> облигационный движок. Облигации/фонды в иностранной валюте — вне скоупа (не добавлять). Без UI
> (задача 56), кроме того, что требует компиляция фронта при смене DTO (типы `api/types.ts`).

---

## Проблема

Владелец несколько месяцев назад покупал юани (CNY, инструмент T-Invest `CNYRUB_TOM`). Сейчас синк
отбрасывает валютные операции и позиции (`BondSyncService` ~:147: тип известен и не облигация/фонд →
пропуск; `GetPortfolioAsync` берёт только bond/etf), поэтому:
- покупка/продажа юаней и курсовая разница не видны в «Доходах» и в XIRR портфеля;
- на «Обзоре» юани спрятаны внутри «Денег на счёте»: `accounts.cash_rub` = `TotalAmountCurrencies` —
  ВСЕ валюты, пересчитанные брокером в рубли; отдельно их не видно.
Плюс скрытый риск: `TInvestPortfolioClient.GetOperationsAsync` (~:179) пишет `item.Payment.ToDecimal()`
в `PaymentRub`, не проверяя валюту платежа (`MoneyValue.Currency`). Операция с платежом в CNY (комиссия
в юанях, купон валютной бумаги) была бы молча посчитана как рубли.

## Что сделать

### A. Схема — миграция `025_currency_assets.sql`

```sql
currencies (
  id PK, figi VARCHAR(32) NOT NULL uq, iso_code VARCHAR(8) NOT NULL uq,   -- 'cny' → храним верхним регистром 'CNY'
  ticker VARCHAR(32) NULL,          -- 'CNYRUB_TOM'
  name VARCHAR(255) NULL,
  moex_secid VARCHAR(32) NULL,      -- для истории курса (CNYRUB_TOM на CETS)
  lot DECIMAL(20,6) NULL, created_at, updated_at
)
currency_positions (
  id, account_id FK, currency_id FK,
  quantity DECIMAL(20,6) NOT NULL,              -- ЕДИНИЦЫ валюты (юани), не лоты
  avg_purchase_price_rub DECIMAL(20,6) NULL,    -- РУБЛИ за 1 единицу (как отдал брокер)
  current_price_rub DECIMAL(20,6) NULL,         -- РУБЛИ за 1 единицу
  price_as_of DATETIME NULL, updated_at,
  UNIQUE (account_id, currency_id)
)
currency_operations (
  id, account_id FK, currency_id NULL FK (ON DELETE SET NULL),
  type ENUM('Buy','Sell','Tax','Fee','Income') NOT NULL,   -- Income: проценты на остаток, если брокер их отдаёт
  date DATETIME NOT NULL,
  amount_rub DECIMAL(20,6) NOT NULL,   -- знак брокера, РУБЛИ (после проверки валюты платежа)
  quantity DECIMAL(20,6) NULL,         -- единицы валюты
  external_id VARCHAR(64) NOT NULL uq, created_at,
  KEY (account_id, date)
)
currency_rate_history (
  currency_id FK, trade_date DATE, close_rub DECIMAL(20,6) NOT NULL,   -- РУБЛИ за 1 единицу
  UNIQUE (currency_id, trade_date)
)
```
Обновить `MigrationIdempotencyTests`.

### B. Коннектор T-Invest

1. **Валюта платежа операции.** В `TInvestOperation` добавить `PaymentCurrency` (ISO, нижний регистр
   как у брокера). В синке (облигации, фонды, валюта) операция с `PaymentCurrency` ≠ `rub` НЕ пишется
   как рубли: конвертировать по курсу дня из `currency_rate_history` (если есть) или пропустить с
   LogWarning + счётчиком в `SyncResult.Errors` («операция в CNY без курса — пропущена»). Конверсию —
   одной чистой функцией с doc-comment'ом. Юнит-тесты.
2. `TInvestPortfolio.Currencies` — позиции `InstrumentType == "currency"`, **кроме рубля** (figi
   `RUB000UTSTOM` / iso `rub`). Единицы: `Quantity` — единицы валюты, `CurrentPrice`/`AveragePositionPrice` —
   рубли за единицу (проверить по SDK doc; если `AveragePositionPrice` отдаётся в другом виде —
   зафиксировать в README коннектора). Существующие `CashRub`/`TotalPortfolioRub` не меняются.
3. `GetCurrencyInfoAsync(figi)` — `CurrencyByAsync` → iso_currency_name, ticker, name, lot.
4. `TInvestCurrencyOperationMapper`: Buy*/Sell* → Buy/Sell; налоговые → Tax; комиссионные → Fee;
   процент на остаток (если есть такой тип в SDK — сверить рефлексией/XML, напр. `OverNight`/
   `AccruingVarmargin` НЕ брать) → Income только при уверенности, иначе null. Имена — по SDK.
5. README коннектора: единицы валютных позиций/операций, валюта платежа.

### C. Синк

`CurrencySyncService` по образцу `FundSyncService` (вызывается из `BondSyncService` на уже полученных
данных, сбой → `Валюта: ...` в ошибках, облигации не страдают): резолв по FIGI (`GetCurrencyInfoAsync`,
справочник раз в сутки), upsert позиций и операций, сверка позиций с защитой «пустой ответ —
удалять со второго подряд» (отдельный static-словарь). В `BondSyncService`: операции с
`InstrumentKind == "Currency"` → в валютный список (не в облигационный журнал).
Суточные рыночные данные: в `FundMarketDataRefresher` (или соседний `CurrencyRateRefresher`, тем же
шагом цикла) — история курса с MOEX `GetCurrencyHistoryAsync(secid, from, to)` →
`/iss/history/engines/currency/markets/selt/boards/CETS/securities/{secid}.json`, колонка `CLOSE` —
**рубли за 1 единицу** (для CNYRUB_TOM ≈ 12.2; у валют с номиналом ≠ 1 (напр. JPY за 100) — учесть
`FACEVALUE`/лот: зафиксировать правило и тест на фикстуре). Первый раз — 2 года, далее инкремент;
ворота как у фондов (включая «пустые» фонды — дата попытки). Фикстура реального JSON.

### D. Журнал доходов и баланс

1. `AssetKind.Currency`. `LedgerEntryMapper` для валютных операций: Buy/Sell/Tax/Fee/Income.
   `PositionIncomeCalculator` переиспользуется как есть (average cost в рублях за единицу; НКД нет;
   `Income` — проценты на остаток). Курсовая разница = Unrealized/Realized.
2. `IncomeLedgerBuilder`: валюты счёта; цена единицы — одна точка `ResolveCurrencyPriceAsync`
   (`current_price_rub` позиции → последний `close_rub` истории → нет = 0 + dataIncomplete), как у фондов.
   Остаток по журналу ≠ позиции (валюта потрачена на неучтённый актив/выведена) → `HasUnknownLots`,
   но НЕ списывать «висящую» себестоимость в убыток, если позиция по-прежнему держится (qty > 0).
3. `/api/income`: `assetKind=currency` (+ `all` включает валюту), DTO `assetKind: "currency"`,
   `currencyId?`, ledger `/api/income/currency/{id}/ledger`. Помесячный ряд и `period`: реализованная
   курсовая разница — в существующее `realizedPnlRub` (новых полей под неё не заводить); проценты на
   остаток — новое поле `currencyIncomeGrossRub` (0, если их нет); налоги/комиссии — в общие.
4. `/api/portfolio/summary`: новые поля `currencyValueRub` (Σ qty × цена) и `cashRubOnly`
   (= `cashRub − currencyValueRub`, не ниже 0; doc-comment: брокерский `TotalAmountCurrencies`
   уже включает валюту — без вычитания будет двойной счёт). `totalRub` = облигации + фонды +
   `cashRub` (как сейчас — валюта уже внутри cashRub), то есть итог НЕ меняется, меняется разбивка.
   XIRR портфеля включает валютные потоки и терминальную стоимость валюты.
5. `GET /api/currencies` → `{ asOf, currencies: [{ currencyId, isoCode, ticker, name, quantity,
   currentPriceRub, priceAsOf, marketValueRub, avgCostRub, investedRub, unrealizedPnlRub,
   unrealizedPnlFraction, realizedPnlRub, totalResultRub, totalReturnFraction, xirrFraction,
   rateChange: { m1, m3, m12 } (ДОЛИ изменения курса, null при нехватке истории), rateHistory
   (≤ 1 года, {date, closeRub}), hasUnknownLots, dataIncomplete }], totals, disclaimer }` —
   включая проданную валюту, если по ней есть операции (qty 0, итог по реализованной разнице).
6. `api/types.ts` — синхронно с DTO (только типы, без UI).
7. Гайд: раздел «Валюта — отдельный домен (задача 55)», единицы, правило «cashRub включает валюту».

### E. Тесты

- Юнит: маппер валютных операций; конверсия валюты платежа (CNY по курсу дня; нет курса → пропуск
  с ошибкой); калькулятор на эталоне: купил 1 000 CNY по 13.00 (−13 000), курс 12.20 → unrealized −800;
  продал 400 по 12.50 → realized 400×(12.50−13.00) = −200, инвариант итога; парсинг истории курса
  (фикстура, номинал/лот); `CurrencySyncService` (создание, сверка, пустой ответ со второго раза);
  `BondSyncService` — валютные операции уходят в валюту, не в облигационный журнал.
- Интеграционные: `/api/income?assetKind=currency` и `all` с валютной позицией; `/ledger`;
  `/api/currencies`; `/api/portfolio/summary` — `cashRubOnly + currencyValueRub = cashRub`,
  итог не изменился; 401-теория дополнена новыми маршрутами; миграция 025.

## Критерии приёмки

- [ ] После синка покупка/продажа юаней видна в журнале валют; курсовая разница — в «Доходах»
      (реализованная/бумажная), в итогах и XIRR портфеля.
- [ ] Баланс: юани показаны отдельной суммой, рубли — без них; общий итог не задвоен.
- [ ] Операции с платежом не в рублях больше не записываются как рубли.
- [ ] Облигационный/фондовый синк и существующие тесты не изменили поведения.
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
