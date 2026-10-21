# Задача 51 — Фонды (БПИФ/ETF): схема, синк позиций/операций, рыночные данные

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (паттерны и контракт единиц — читать обязательно).
>
> **Зависимости:** нет. Потребители результата — задачи 52 (журнал доходов), 53 (аналитика и UI фондов).
> **Порядок в волне:** первая задача цикла «фонды и доходы» (51 → 52 → 53 → 54, строго последовательно).
>
> **Рамки (что НЕ делать):** фонды — ОТДЕЛЬНЫЙ домен. Не класть их в `instruments`/`positions`/
> `operations` и не трогать облигационный движок (YTM, скоркарта, вердикты, замены, сигналы,
> cash-flow, XIRR/снимки облигаций, бэкфилл истории). Акции/валюта по-прежнему пропускаются.
> UI и аналитики фондов здесь нет (задача 53) — только данные и их наполнение.

---

## Проблема

Пользователь купил паи БПИФ Т-Капитала — «Пассивный доход» (TPAY, ISIN RU000A108WX3, ежемесячные
выплаты) и «Т-Капитал ОФЗ» (TOFZ, ISIN RU000A10A1L8, накопительный, ОФЗ). Сейчас синк их
выбрасывает: `TInvestPortfolioClient.GetPortfolioAsync` (~:108) оставляет только
`InstrumentType == "bond"`, а `BondSyncService.SyncAsync` (~:147) пропускает операции, у которых
тип инструмента известен и это не облигация. В итоге фонды не видны ни в стоимости портфеля, ни в
доходах. Нужен слой данных под фонды: позиции, журнал операций (покупки/продажи/выплаты/налог/
комиссии), справочник фонда (тикер, SECID, комиссия управления), история цен и выплат, доходность
индекса ОФЗ для прогноза.

Проверено вручную (2026-10-21): MOEX ISS отдаёт оба фонда на доске **TQBR** рынка `shares`
(`/iss/engines/stock/markets/shares/boards/TQBR/securities/TPAY.json`), цены — **рубли за пай**;
история индекса RGBI (`/iss/history/engines/stock/markets/index/securities/RGBI.json`) несёт
`YIELD` (**проценты**) и `DURATION` (**дни**). SDK `Tinkoff.InvestApi` 0.6.22.1 содержит
`Instruments.EtfByAsync`, `Instruments.GetAssetByAsync` (→ `AssetEtf.TotalExpense`),
`Instruments.GetDividendsAsync`.

## Что сделать

### A. Схема — миграция `023_funds.sql`

Таблицы (MySQL-стиль гайда, snake_case, `uq_*`/`idx_*`/`fk_*`):

```sql
funds (
  id BIGINT UNSIGNED AUTO_INCREMENT PK,
  figi VARCHAR(32) NOT NULL,                 -- uq
  isin VARCHAR(12) NOT NULL,                 -- uq
  ticker VARCHAR(32) NULL,
  secid VARCHAR(32) NULL,                    -- MOEX SECID (для TPAY/TOFZ совпадает с тикером)
  name VARCHAR(255) NULL,
  management_company VARCHAR(255) NULL,
  focus_type VARCHAR(64) NULL,               -- как отдал T-Invest (Etf.FocusType: "fixed_income", "equity"...)
  total_expense_fraction DECIMAL(9,6) NULL,  -- ДОЛЯ годовых (0.009 = 0.9%)
  forecast_method ENUM('Auto','Distributions','IndexYield','TrailingReturn') NOT NULL DEFAULT 'Auto',
  benchmark_index VARCHAR(32) NULL,          -- код индекса MOEX для IndexYield (напр. 'RGBI'); NULL = авто
  created_at, updated_at
)
fund_positions (
  id, account_id FK accounts, fund_id FK funds,
  quantity DECIMAL(20,6) NOT NULL,
  avg_purchase_price_rub DECIMAL(20,6) NOT NULL,   -- рубли за пай (как отдал брокер)
  current_price_rub DECIMAL(20,6) NULL,            -- рубли за пай, T-Invest GetPortfolio.CurrentPrice
  price_as_of DATETIME NULL,
  updated_at,
  UNIQUE uq_fund_positions_account_fund (account_id, fund_id)
)
fund_operations (
  id, account_id FK, fund_id NULL FK (ON DELETE SET NULL),
  type ENUM('Buy','Sell','Distribution','Tax','Fee','SecurityIn','SecurityOut') NOT NULL,
  date DATETIME NOT NULL,
  amount_rub DECIMAL(20,6) NOT NULL,   -- знак как отдал брокер (покупка −, выплата +), НЕ переписывать
  quantity DECIMAL(20,6) NULL,
  external_id VARCHAR(64) NOT NULL,    -- uq: id операции T-Invest
  created_at,
  KEY idx_fund_operations_account_date (account_id, date)
)
fund_price_history (
  fund_id FK, trade_date DATE, close_rub DECIMAL(20,6) NOT NULL,
  UNIQUE uq_fund_price_history (fund_id, trade_date)
)
fund_distributions (                  -- выплаты фонда на один пай (рыночный факт, не зависит от владения)
  fund_id FK, record_date DATE NOT NULL, payment_date DATE NULL,
  amount_per_share_rub DECIMAL(20,6) NOT NULL,
  UNIQUE uq_fund_distributions (fund_id, record_date)
)
market_index_history (                -- доходность/дюрация облигационных индексов MOEX
  index_code VARCHAR(32), trade_date DATE,
  close_value DECIMAL(20,6) NULL,
  yield_fraction DECIMAL(12,8) NULL,    -- ДОЛЯ (ISS YIELD % ÷ 100)
  duration_years DECIMAL(12,6) NULL,    -- ГОДЫ (ISS DURATION дни ÷ 365)
  UNIQUE uq_market_index_history (index_code, trade_date)
)
```

Обновить `MigrationIdempotencyTests` (число + список файлов).

### B. Модели и репозитории

1. `Bonds.Core/Models`: `Fund`, `FundPosition`, `FundOperation` (+ enum `FundOperationType`),
   `FundPricePoint`, `FundDistribution`, `MarketIndexPoint`, enum `FundForecastMethod`. Doc-comment
   единиц на каждом денежном/долевом поле.
2. Интерфейсы в `Bonds.Core/Interfaces/Repositories`, реализации в `Bonds.Infrastructure/Repositories`
   по образцу `InstrumentRepository`/`OperationRepository` (Dapper, `SelectColumns`, upsert с
   `LAST_INSERT_ID`, батчевые upsert для истории — образец `BondUniverseRepository`, enum → строкой
   в параметрах). Минимальный набор: `IFundRepository` (GetById/GetByFigi/GetByIsin/GetAll/Upsert/
   UpdateSettings(forecastMethod, benchmarkIndex)), `IFundPositionRepository` (GetByAccount/Upsert/
   DeleteAllExcept(accountId, fundIds)), `IFundOperationRepository` (UpsertManyByExternalId/
   GetByAccount/GetByFund), `IFundMarketDataRepository` (prices: UpsertMany/GetRange/GetLastDate;
   distributions: UpsertMany/GetByFund; index: UpsertMany/GetLatest(indexCode)/GetLastDate).
3. DI в `DependencyInjection.cs` — ленивый `GetConnStr`, как у остальных.

### C. T-Invest коннектор

1. `TInvestPortfolio` получает `Funds: IReadOnlyList<TInvestPortfolioPosition>` — позиции с
   `InstrumentType == "etf"` (регистронезависимо). `Bonds` не меняется. `CurrentPrice`/
   `AveragePositionPrice` у фонда — **рубли за пай** (GetPortfolio, конверсии нет; `CurrentNkd` у
   фонда не используется).
2. `TInvestOperation` уже несёт `InstrumentKind`; добавить `IsFund => InstrumentKind == "Etf"`
   (регистронезависимо, как `IsBond`).
3. Новые методы `ITInvestPortfolioClient`:
   - `GetFundInfoAsync(figi)` → `TInvestFundInfo { Isin, Ticker, Name, FocusType, TotalExpenseFraction? }`:
     `EtfByAsync(IdType=Figi)` → isin/ticker/name/focus_type/asset_uid; затем `GetAssetByAsync(asset_uid)`
     → `AssetEtf.TotalExpense`. **Единицы TER не подтверждены документацией**: конверсия в ДОЛЮ —
     отдельной чистой функцией `TInvestNumeric.ExpenseRatioToFraction(raw)`: `raw > 0.05` → проценты
     (`raw/100`), `0 < raw ≤ 0.05` → уже доля, `≤ 0` или `> 10` → null + LogWarning. Doc-comment с
     обоснованием (реальные TER 0.1–3%; TER < 0.05% не бывает). Сбой второго вызова — TER null, не ошибка.
   - `GetFundDistributionsAsync(figi, from, to)` → `GetDividendsAsync` → список
     `(RecordDate, PaymentDate?, AmountPerShareRub)` (поле `DividendNet`, рубли за пай). Ошибка gRPC →
     пустой список + LogWarning.
4. `TInvestFundOperationMapper.Map(string)` (чистая функция, рядом с `TInvestOperationMapper`):
   Buy/BuyCard/BuyMargin → Buy; Sell* → Sell; `Dividend`, `DividendTransfer`, `DivExt` → Distribution
   (выплата фонда); все налоговые типы (как в облигационном маппере, включая `DividendTax*`) → Tax;
   комиссионные → Fee; InputSecurities/OutputSecurities → SecurityIn/Out; прочее → null. Имена enum
   сверить рефлексией по SDK (как сделано для облигационного маппера; список зафиксировать в
   `Connectors/TInvest/README.md`).

### D. Синк

1. Новый `FundSyncService` (`Bonds.Infrastructure/Sync`), вызывается **из `BondSyncService.SyncAsync`**
   на уже полученных данных (не делать второй GetPortfolio/GetOperations):
   `ApplyAsync(accountId, IReadOnlyList<TInvestPortfolioPosition> funds, IReadOnlyList<TInvestOperation> fundOps, ct)`.
   В `BondSyncService` при переборе операций: `op.IsFund` → в список для фондов (дальше в облигационный
   журнал не идёт, как и сейчас). Сбой `FundSyncService` → `result.Errors.Add("Фонды: ...")`, облигационный
   синк не страдает (try/catch вокруг вызова).
2. Внутри: резолв фонда по FIGI (`GetByFigi`, иначе `GetFundInfoAsync` → upsert `funds`; SECID = тикер,
   проверить через MOEX `ResolveSecidByIsinAsync` если он подходит для паёв, иначе тикер), кэш FIGI
   в пределах синка; upsert позиций; upsert операций по `external_id`; сверка позиций по образцу
   облигационной (`DeleteAllExcept`, включая защиту «пустой ответ брокера при непустом локальном
   портфеле — удалять со второго подряд» — отдельный static-словарь для фондов) — закрытые фонды
   исчезают из `fund_positions`, но их операции остаются (их покажет задача 52).
3. Операции фонда, у которых FIGI фонда не резолвится, сохраняются с `fund_id = NULL` (не теряем деньги).
4. Справочник фонда обновлять раз в сутки (TER/имя), не на каждом синке: если `funds.updated_at`
   < 24 ч — пропуск.

### E. Рыночные данные фондов (суточный шаг)

1. MOEX `IMoexIssClient`:
   - `GetSharesHistoryAsync(secid, from, to)` → `(TradeDate, CloseRub?)` с доски TQBR рынка shares
     (пагинация `history.cursor` — тем же паттерном, что `GetHistoryPricesAsync`). Цена — **рубли**,
     конверсии нет (в отличие от облигаций, где % номинала!) — doc-comment.
   - `GetIndexHistoryAsync(indexCode, from, to)` → `(TradeDate, Close?, YieldFraction?, DurationYears?)`
     с `/iss/history/engines/stock/markets/index/securities/{code}.json`: `YIELD` % → доля (÷100),
     `DURATION` дни → годы (÷365). Фикстура реального JSON в `tests/Bonds.Tests/Fixtures/Moex/`.
2. `FundMarketDataRefresher` (`Bonds.Infrastructure/Sync`), шаг `SyncCycleService` после шага 1
   (рядом с `RefreshRiskLevelsAsync`), ворота «раз в сутки» по БД (последняя дата в истории):
   для каждого фонда из `funds` (держим сейчас ИЛИ держали — все строки справочника) — история цен
   (первый раз 2 года назад, далее с последней даты), выплаты через T-Invest за 2 года (upsert);
   индекс `RGBI` — история за 2 года/инкремент. Сбой — LogWarning, цикл не помечается неуспешным
   (как risk levels). Без токена T-Invest — выплаты пропускаются, MOEX-часть работает.
3. Обновить `Connectors/TInvest/README.md` и `docs/CODEBASE-GUIDE.md`: таблица единиц (цены паёв
   MOEX/T-Invest — рубли; TER — доля после конверсии; индекс YIELD/DURATION), раздел «Фонды —
   отдельный домен» (2–4 предложения).

### F. Тесты

- Юнит: `TInvestFundOperationMapper` (все ветки, включая Dividend → Distribution, DividendTax → Tax,
  неизвестное → null); `ExpenseRatioToFraction` (0.9 → 0.009; 0.009 → 0.009; 0 → null; 50 → null);
  парсинг `GetIndexHistoryAsync`/`GetSharesHistoryAsync` на фикстурах (FakeHandler, пагинация);
  `FundSyncService` с моками: создаёт фонд, пишет позицию и операции, сверка удаляет проданный фонд,
  пустой ответ брокера удаляет только со второго раза; `BondSyncService`: операции Etf уходят в
  фонды, облигационный журнал их не получает, облигационные позиции не меняются.
- Интеграционные (Testcontainers): репозитории — upsert идемпотентен по external_id/(fund_id,trade_date);
  `DeleteAllExcept` по фондам не задевает чужой счёт; миграция 023 в `MigrationIdempotencyTests`.

## Критерии приёмки

- [ ] После синка с портфелем, где есть TPAY/TOFZ, в `funds`/`fund_positions`/`fund_operations`
      появляются фонды, их позиции (рубли за пай) и операции, включая выплаты (Distribution) и налог.
- [ ] Облигационные таблицы/журнал/XIRR не получают ни одной фондовой записи; существующие тесты
      облигационного синка зелёные без ослабления.
- [ ] Суточный шаг наполняет историю цен фондов (рубли), выплаты на пай и историю RGBI (доля/годы).
- [ ] Сбой любой фондовой части не роняет облигационный синк и не помечает цикл неуспешным
      из-за рыночных данных.
- [ ] Гайд и README коннектора обновлены (единицы!).
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
