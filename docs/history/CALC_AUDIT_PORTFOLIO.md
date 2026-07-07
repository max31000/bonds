# Аудит корректности — портфельно-денежный слой + свип единиц на границах

> **Зона.** `Bonds.Core/Analytics` + `Bonds.Core/CashFlow` + оркестраторы в `Bonds.Infrastructure`
> (`PositionCostBasisService`, `PortfolioXirrService`, `PortfolioHistoryRebuildService` +
> `PortfolioHistoryBackfillService`, `IfSoldNowService`, `SwitchAnalysisService`,
> `CashAllocationService`, `IntradaySeriesBuilder`, `CashFlowProjectionService`/`CashFlowAggregator`,
> `PortfolioCompositionService`, `RateScenarioService`, `PortfolioTrajectoryService`,
> `WatchlistSyncService`, эндпоинты `LiveEndpoints`/`AnalyticsEndpoints`) + фронтовые расчёты
> (`utils/format.ts`, `utils/positionsAggregation.ts`, `utils/yieldHeatmap.ts`,
> `utils/scatterChartData.ts`, `store/useRecommendationsStore.ts`).
>
> **Метод.** Построчное чтение всего кода в зоне + `CORRECTNESS_AUDIT.md`/`plan/12` (проверка
> регресса T-1…T-10) + `bond-portfolio-analytics-spec.md`. Кросс-чек синтетического счёта
> (3 покупки разными лотами, частичная продажа, купоны) вручную и независимым python
> Ньютон-Рафсон решателем XIRR (Actual/365, тот же day-count, что `XirrCalculator`) —
> скрипт в scratchpad аудита, не в репо. Проверка единиц на всех границах T-Invest/MOEX/БД/DTO/
> фронт через чтение генерируемых protobuf-типов T-Invest SDK (`ilspycmd`-декомпиляция
> `Tinkoff.InvestApi.dll` — различение `MoneyValue` (рубли) от `Quotation` (пункты/% номинала)
> по каждому полю, которое читает код синка).
>
> **Итог одной фразой.** Регрессий по T-1…T-10 (`plan/12-correctness-fixes.md`) не найдено — все
> проверены построчно и подтверждены доп. тестами (см. «Проверено и чисто»). Один уже известный и
> чинящийся параллельно баг единиц (`GetLastPrices` → `Quotation` в пунктах трактуется как рубли в
> `LiveQuotesPollingService`/`BondSyncService` части 3, `CleanPrice = q.LastPrice`) — **не репортится
> повторно** по инструкции задания, подтверждён как единственное затронутое место (декомпиляцией
> SDK доказано, что основной путь синка позиций через `GetPortfolioAsync().CurrentPrice`
> использует `MoneyValue`, т.е. уже рубли, и не задет). Новых CRITICAL/MAJOR багов в зоне аудита
> **не найдено**. Есть несколько MINOR/INFO находок — в основном недокументированные исключения из
> конвенции «бэкенд = доли» и допущения, которые стоит явно закрепить doc-comment'ами/тестами
> (часть уже закреплена этим аудитом).

---

## Сводка находок по серьёзности

| ID | Severity | Область | Кратко |
|----|----------|---------|--------|
| P-1 | 🟡 MINOR | Единицы/конвенция | `RateScenarioService.DeltaPercent` и `PortfolioCompositionService.SharePercent` — единственные "Percent"-поля бэкенда, которые уже **готовые проценты** (×100), а не доли — нарушение задокументированной конвенции репо («бэкенд = доли, *100 делает фронт»). Работает корректно только потому, что фронт для этих двух полей сознательно НЕ вызывает `formatPercent` (использует `.toFixed()`/`formatSharePercent`) — хрупкая точка: если кто-то отрефакторит один конец пары не заметив другого, число тихо станет в 100 раз меньше/больше на экране. |
| P-2 | ℹ️ INFO | Единицы/наименование | `LiveEndpoints.LivePositionRowDto.ChangeDayPercent` (и аналогично `Dashboard.tsx` локальный `deltaPercent`) называется «…Percent», но фактически несёт **долю** (0.01 = 1%) — соответствует общей бэкенд-конвенции (фронт делает `formatPercent`⇒×100), но имя поля само по себе вводит в заблуждение при чтении без контекста (в отличие от P-1, где имя тоже "Percent", но по факту уже проценты). Рекомендация на будущее: суффикс `Percent` в DTO стоит зарезервировать только за уже-процентными полями (как в P-1), а доли называть `...Fraction`/без суффикса — сейчас оба варианта используют один и тот же суффикс с противоположным смыслом. |
| P-3 | ℹ️ INFO | Аллокация | `CashAllocationCandidate.LotSize` всегда захардкожен в `1m` с `LotSizeIsAssumed=true` на обоих call-сайтах в `AnalyticsEndpoints.GetAllocation` (строки ~557, ~603) — не баг (домен `Instrument` физически не хранит размер лота, допущение честно помечено флагом и видно на фронте), но входит в явный список аудита («допущение lotSize=1») — фиксирую как подтверждённое и осознанное ограничение, не случайную недоделку. Если когда-нибудь захочется докупать бумаги с лотом >1 (напр. некоторые ОФЗ/евробонды с лотом 1000), результат аллокации будет предлагать в 1000 раз больше "лотов", чем реально можно купить одной заявкой — стоит иметь в виду при добавлении ДЕЙСТВИЙ с портфелем (реального размещения заявок), а не только для дисплейной оценки. |
| P-4 | ℹ️ INFO | Cost basis / купоны | `PositionCostBasisService` не вычитает налог с купона отдельно (см. doc-comment сервиса, строки 27-35) — все `Tax`-операции (включая `TaxCorrectionCoupon`) идут в `OperationType.Tax` и не участвуют ни в `CouponsReceivedRub`, ни где-либо ещё. Это уже задокументированное и осознанное упрощение (не новая находка), но стоит явно перепроверить перед добавлением ДЕЙСТВИЙ с портфелем: если купон брокер платит "грязным" (без вычета НДФЛ) в T-Invest API для конкретного инструмента/периода, `CouponsReceivedRub`/`TotalReturnRub` будут завышены — не наблюдалось в коде, но не проверяемо без боевых данных по конкретному инструменту. |

**Ничего уровня CRITICAL/MAJOR не найдено.** Ядро расчётов (average cost, XIRR, восстановление
истории, forward-fill, композиция, аллокация, switch-анализ, cash-flow проекция) корректно и
согласовано по единицам на всех проверенных путях.

---

## Таблица единиц на границах системы

| Граница / поле | Единица на источнике | Конверсия | Куда идёт | Статус |
|---|---|---|---|---|
| T-Invest `GetPortfolioAsync().Positions[].CurrentPrice` | `MoneyValue` (рубли, за 1 бумагу) | нет — уже рубли | `MarketQuote.CleanPrice` (`BondSyncService.cs:160`) | ✅ корректно (декомпиляция SDK подтвердила `MoneyValue`) |
| T-Invest `GetPortfolioAsync().Positions[].CurrentNkd` | `MoneyValue` (рубли) | нет | `MarketQuote.Accrued`/`Position.Accrued` | ✅ корректно |
| T-Invest `Operations[].Payment` | `MoneyValue` (рубли, со знаком потока) | нет | `Operation.AmountRub` (`BondSyncService.cs:107`) | ✅ корректно, знак — источник истины (см. `PortfolioXirrService.SignedAmount` doc-comment) |
| T-Invest `MarketData.GetLastPrices().Price` | `Quotation` (**пункты/% номинала**, НЕ рубли) | **отсутствует** | `MarketQuote.CleanPrice` (`BondSyncService.cs:219`, fallback часть 3) и `IntradayQuote.DirtyPriceRub` (`LiveQuotesPollingService.cs`) | 🔧 известный баг, чинится параллельно — не репортится повторно (см. итог) |
| MOEX ISS `bondization.coupons[].value_rub` | рубли (уже с учётом лота/номинала выпуска) | нет | `CouponSchedule.ValueRub` | ✅ корректно (T-1 из plan/12, пагинация тоже проверена — фиксов регресса нет) |
| MOEX ISS `securities.json PREVPRICE/PREVWAPRICE` | % от номинала | `%/100 * FaceValue` (`WatchlistSyncService.cs:104`) | `MarketQuote.CleanPrice` | ✅ корректно |
| MOEX ISS `history.json CLOSE` | % от номинала | `%/100 * FaceValue` (`PortfolioHistoryBackfillService.cs:154`) | карта `priceHistory` → `PortfolioHistoryRebuildService` | ✅ корректно |
| MOEX ISS `history.json ACCINT` | рубли (НКД уже в рублях) | нет, прибавляется как есть | `dirtyPriceRub` (там же) | ✅ корректно |
| `market_quotes.clean_price/dirty_price/accrued` (БД) | рубли (по конвенции, без суффикса в имени колонки) | — | везде вниз по цепочке как рубли | ✅ корректно и последовательно используется |
| `intraday_quotes.dirty_price_rub` (БД) | рубли (суффикс явный) | — | `IntradaySeriesBuilder` | ✅ корректно |
| `instrument_price_history.close_price_percent` (БД) | % от номинала (суффикс явный) | `%/100 * faceValue` на фронте (`PositionDetail.tsx:146`) | график цены карточки позиции | ✅ корректно, конверсия на фронте, не на бэке — задокументировано |
| `portfolio_value_snapshots.market_value_rub/invested_rub` (БД) | рубли | — | `/api/analytics/xirr` | ✅ корректно |
| `projected_cash_flows.gross_rub/tax_rub/net_rub` (БД) | рубли | — | `/api/cashflow/*` | ✅ корректно |
| API DTO: `ytmEffective`, `currentYield`, `gSpread`, `unrealizedPnlPercent`, `totalReturnPercent`, `realizedPnlPercent`, `xirr`/`currentXirr`, `deltaPercent` (Dashboard, самостоятельный расчёт) | **доля** (0.12 = 12%) | фронт: `formatPercent` (×100) | таблица позиций, карточка позиции, XIRR-виджет, Dashboard KPI | ✅ корректно, конвенция соблюдена |
| API DTO: `sharePercent` (composition) | **уже проценты** (0-100) | фронт: `formatSharePercent` (без ×100, отдельный форматтер) | Dashboard pie, Analytics composition, `useRecommendationsStore` (доля эмитента в бейдже) | ⚠️ P-1 — исключение из конвенции, но внутренне согласовано (выделенный форматтер) |
| API DTO: `rateScenario.scenarios[].deltaPercent` | **уже проценты** (0-100) | фронт: `.toFixed(2)` напрямую, БЕЗ `formatPercent` | Analytics.tsx bar chart/tooltip | ⚠️ P-1 — то же исключение, задокументировано новым тестом `DeltaPercent_IsAlreadyInPercentUnits_NotFraction` |
| API DTO: `changeDayPercent` (LiveEndpoints) | доля (0.01 = 1%) | фронт: `formatPercent` | live-бейдж дневного изменения | ✅ корректно по сути, но имя вводит в заблуждение (P-2) |
| `SwitchAnalysisService`/`CashAllocationService` вход `EffectiveYield` | доля | нет (используется как множитель) | внутренние формулы | ✅ корректно |
| `CashAllocationCandidate.PricePerLotRub` | рубли (уже с комиссией, `pricePerUnitRub * (1+ставка)`) | — | сравнение с `leftover`/`amountRub` (рубли) | ✅ корректно, единицы согласованы |

---

## Кросс-чек синтетического счёта (ручной + python-оракул)

Сценарий (один инструмент, 3 покупки разными лотами/ценами, частичная продажа, 2 купона):

```
2025-01-10  Buy   10 @ 1000/шт   AmountRub = -10000
2025-02-10  Buy   10 @ 1100/шт   AmountRub = -11000
2025-03-10  Buy    5 @ 1200/шт   AmountRub =  -6000
2025-04-10  Sell   8 @ 1150/шт   AmountRub =  +9200   (частичная продажа)
2025-06-10  Coupon                AmountRub =   +300
2025-09-10  Coupon                AmountRub =   +300
asOf = 2025-12-10, остаток 17 шт, грязная цена 1250/шт → marketValueRub = 21250
```

**Average cost (ручной расчёт, сверен с сервисом):**
после 3 покупок qty=25, cost=27000 (avg=1080); после продажи 8 шт — avg остаётся 1080 (метод
average cost не меняет среднюю при частичной продаже), qty=17, cost=18360.
→ `AverageCostRub=1080`, `InvestedRub=18360`, `UnrealizedPnlRub=2890`, `CouponsReceivedRub=600`,
`TotalReturnRub=3490`. **Сервис `PositionCostBasisService.Calculate` даёt те же числа до копейки**
(тест `CostBasis_ReferenceJournal_MatchesHandComputedAverageCostAndPnl`).

**XIRR (независимый python Ньютон-Рафсон, Actual/365, те же 7 потоков + терминал 21250 на
2025-12-10):** `XIRR = 0.2487788236` (≈24.88% годовых). **`PortfolioXirrService.Calculate` даёт то
же значение** с точностью 1e-6 (тест `Xirr_ReferenceJournal_MatchesIndependentNewtonSolver`).
Скрипт кросс-чека — `scratchpad/xirr_check.py` (не в репозитории, только временный).

---

## Проверено и чисто (с числами)

- **T-1…T-10 (`plan/12-correctness-fixes.md`) — регресса нет.** Построчно перечитаны все
  соответствующие файлы:
  - T-3/C-1/C-2/H-2/M-2 (траектория): `PortfolioTrajectoryService.BuildTrajectory` — тело
    переносится из `bondValue` в `cash` (не задваивается), доход = только купон-нетто, итерация
    стартует с `i=0` (текущий месяц включён). Существующие тесты `Redemption_DoesNotDoubleCountPrincipal`,
    `Income_ExcludesPrincipal`, `CurrentMonthIncluded`, `MonotonicWithNoFlows` подтверждают.
  - T-4/H-1/M-1 (сценарий ставок): `RateScenarioService.Compute` — база `currentValue` = весь
    портфель (не подмножество с дюрацией), `NewValueRub == CurrentValueRub + DeltaRub` всегда
    (тест `BaseValueIncludesAllHoldings`: currentValue=2000 (вкл. флоатер), DeltaRub=-30,
    NewValue=1970 — сходится).
  - T-5/M-3 (единое «сегодня»): траектория и эндпоинты принимают `asOf`/`from` через
    `BusinessClock.MoscowToday()` явно параметром, не `DateTime.Today`/`UtcNow` вразнобой.
  - T-6/M-5 (линкеры): вне периметра фактических изменений этого аудита (нет линкеров в тестовых
    данных), но код `CashFlowProjectionService`/`BondCashFlowBuilder` не тронут этим раундом —
    не проверялся заново по существу.
  - T-7/L-1 (scatter ↔ G-спред): `scatterChartData.ts` строит точки по `macaulayDuration`
    (`durationYears: p.macaulayDuration`), тот же измеритель, что и G-спред на бэкенде — согласовано.
  - T-8/L-3, T-9/L-4: `CurrentYieldCalculator`/`DurationCalculator` вне зоны данного аудита
    (Calculation, не Analytics/CashFlow) — не проверялись повторно по существу, только
    подтверждено, что коммиты T-8/T-9 присутствуют в истории (`4fd5927`, `9b45fda`).
  - T-10/L-5 (switch-анализ): `SwitchAnalysisService.Compare` — спред считается на
    `netProceedsAfterSale` (капитал после комиссии продажи), не на полной стоимости hold.
    Инвариант «спред 0 → выгода = −комиссии» уже покрыт существующим тестом
    `Compare_CommissionsScaleWithBothTrades_NotJustOne` (равные доходности → `NetBenefitRub ≈
    -TotalSwitchCostRub`).

- **Инварианты (все подтверждены тестами, включая новые):**
  - Композиция: `composition.ByIssuer/BySector/ByCouponType/ByDurationBucket.Sum(SharePercent) ==
    100m` ровно (не 99.99/100.01) — `NormalizeShares` кладёт остаток в последний элемент
    (`PortfolioCompositionServiceTests.Calculate_SharesSumTo100Percent_AcrossAllDimensions`).
  - Аллокация: `Σ EstimatedCostRub + LeftoverRub == AmountRub` точно, `LeftoverRub >= 0` —
    проверено на кандидатах с не-круглыми ценами лота (733.33, 1250.50, 999.99, 421.17, 10000) и
    отдельно в ветке немедленной блокировки лидера лимитом концентрации (новые тесты
    `Allocation_AcrossManyCandidates_NeverSpendsMoreThanAmount_LeftoverNonNegative`,
    `Allocation_ConcentrationLimit_LeftoverStillReconciles_WhenLeaderIsBlockedImmediately`).
  - Switch-анализ: спред 0 → `NetBenefitRub == -TotalSwitchCostRub` (существующий тест).
  - Траектория: без потоков стоимость на всех 36 месяцах равна текущей (`MonotonicWithNoFlows`,
    существующий тест) — эквивалент «месяц 0 = текущая стоимость».
  - `changeDayPercent = 0`, когда live-тик численно равен дневной базе (новый тест
    `GetLivePositions_TickEqualsReferencePrice_ChangeDayPercentIsExactlyZero`) — раньше было
    покрыто только ненулевое изменение (+1%), вырожденный случай (тик пришёл, но цена не
    изменилась) не проверялся явно.
  - Cost basis: покупка + полная продажа → `InvestedRub = null` (не 0 — осознанно, "остатка нет"
    ≠ "вложено 0₽"), подтверждено новым тестом
    `CostBasis_BuyThenFullSell_InvestedGoesToZero_NotNull` и уже существующим набором в
    `PositionCostBasisServiceTests`.
  - Средневзвешенная «Итого» на фронте (`positionsAggregation.ts`) — между min и max входящих
    значений по построению (`weightedAverage` — взвешенное среднее, отдельно проверено, что
    флоатеры исключены из доходности, но не из дюрации) — существующее покрытие в
    `positionsAggregation.test.ts` (7 тестов), без пробелов.

- **Краевые случаи:**
  - Пустой журнал → `PositionCostBasisService`/`PortfolioXirrService`/`PortfolioHistoryRebuildService`
    возвращают null/пустой список, не падают и не подставляют 0 молча (уже покрыто существующими
    тестами).
  - Продажа без покупки в журнале: `PositionCostBasisService` → `HasUnknownLots=true`,
    клампит остаток (не уходит в отрицательные числа); `PortfolioXirrService` на единственном
    притоке без оттока/терминала → `null` (новый тест `Xirr_SellWithoutAnyPriorBuy_StillSolvesFromAvailableFlows`
    подтверждает именно это поведение — решатель не выдумывает корень там, где его нет).
  - Отрицательный XIRR: подтверждён сходится и даёт ожидаемое отрицательное значение (новый тест
    `Xirr_NegativeReturn_SolvesToNegativeRate`, потеря 30% за год → XIRR ≈ -30%).
  - Нулевые цены/один тик/NaN-путь DTO→фронт: `formatPercent`/`formatRub`/`formatSharePercent`/
    `formatBp`/`formatNumber` — все возвращают `'—'` на `null`/`undefined`/`NaN`, уже покрыто
    существующим `format.test.ts` (полный набор).
  - `IntradaySeriesBuilder`: один инструмент = его собственные тики (уже покрыто
    `SingleInstrument_ProducesOnePointPerTick`), инструмент без тиков исключён (не 0), сдвинутые
    времена — forward-fill корректен (все — существующее покрытие, без пробелов).
  - `GetLivePositions` без единой котировки (ни intraday, ни market_quotes) — не падает,
    `lastPriceRub=null`, `marketValueRub=0`, `changeDayPercent=null`, `isStale=true` (новый тест
    `GetLivePositions_NoQuoteAtAll_ReturnsNullPriceAndZeroMarketValue_NotThrow`).

- **Единицы на T-Invest границе (декомпиляция SDK, `Tinkoff.InvestApi` 0.6.22.1):**
  подтверждено через `ilspycmd`, что `PortfolioPosition.CurrentPrice`/`CurrentNkd` — тип
  `MoneyValue` (рубли), а `LastPrice.Price` (из `GetLastPrices`) — тип `Quotation` (пункты/% от
  номинала). Основной путь синка позиций (`BondSyncService.cs:160-161`, `GetPortfolioAsync`)
  использует первое (корректно, рубли без конверсии); только fallback-путь
  (`BondSyncService.cs:219`, часть 3, когда портфель не дал `CurrentPrice`) и
  `LiveQuotesPollingService` используют второе без конверсии — это единственный подтверждённый
  баг единиц на этой границе, уже известный и чинящийся параллельно (не репортится повторно по
  условию задания).

---

## Тесты, добавленные этим аудитом

Backend (`tests/Bonds.Tests`, `tests/Bonds.IntegrationTests`), префикс `Audit(portfolio):` в
doc-comment файла/секции:

1. `tests/Bonds.Tests/Analytics/PortfolioMoneyLayerAuditTests.cs` (новый файл, 7 тестов):
   - `CostBasis_ReferenceJournal_MatchesHandComputedAverageCostAndPnl`
   - `Xirr_ReferenceJournal_MatchesIndependentNewtonSolver`
   - `CostBasis_BuyThenFullSell_InvestedGoesToZero_NotNull`
   - `Xirr_SellWithoutAnyPriorBuy_StillSolvesFromAvailableFlows`
   - `Xirr_NegativeReturn_SolvesToNegativeRate`
   - `Allocation_AcrossManyCandidates_NeverSpendsMoreThanAmount_LeftoverNonNegative`
   - `Allocation_ConcentrationLimit_LeftoverStillReconciles_WhenLeaderIsBlockedImmediately`
2. `tests/Bonds.Tests/Analytics/RateScenarioServiceTests.cs` (+1 тест):
   - `DeltaPercent_IsAlreadyInPercentUnits_NotFraction` (закрепляет P-1 как явный контракт)
3. `tests/Bonds.IntegrationTests/LiveEndpointsTests.cs` (+2 теста):
   - `GetLivePositions_TickEqualsReferencePrice_ChangeDayPercentIsExactlyZero`
   - `GetLivePositions_NoQuoteAtAll_ReturnsNullPriceAndZeroMarketValue_NotThrow`

Итого **10 новых тестов**, все зелёные. Фронтовых новых тестов не потребовалось — существующее
покрытие (`positionsAggregation.test.ts`, `format.test.ts`, `Analytics.test.tsx` с фикстурами
`deltaPercent: 6` и т.д.) уже полностью закрывает инварианты из задания на фронтовой стороне;
добавление дублирующих тестов не дало бы новой сигнальной ценности.

---

## Вывод проверок (перед коммитом)

```
dotnet build Bonds.sln -c Release
  → 6 projects, 0 errors, 0 warnings

dotnet test tests/Bonds.Tests/Bonds.Tests.csproj -c Release
  → Пройден!  не пройдено 0, пройдено 318, пропущено 0, всего 318

dotnet test tests/Bonds.IntegrationTests/Bonds.IntegrationTests.csproj -c Release
  → Пройден!  не пройдено 0, пройдено 103, пропущено 0, всего 103

cd bonds-web && yarn typecheck
  → tsc -b --noEmit: Done, 0 errors

cd bonds-web && yarn test:run
  → Test Files  21 passed (21), Tests  195 passed (195)

cd bonds-web && yarn lint
  → 0 errors, 1 pre-existing warning (Login.tsx, react-hooks/exhaustive-deps, не в зоне аудита)
```

Все проверки зелёные, включая 10 новых тестов этого аудита.
