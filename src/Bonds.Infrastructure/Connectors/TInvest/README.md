# T-Invest gRPC контракт — верификация (spec §12.2, plan/04 Часть B п.5)

Этот файл фиксирует, что реально подтверждено в актуальном пакете NuGet `Tinkoff.InvestApi`
(версия `0.6.22.1`, репозиторий `https://opensource.tbank.ru/invest/invest-csharp`) на момент
реализации этапа 04 — отражением собранной сборки SDK (`Tinkoff.InvestApi.dll`), а не "на веру"
по памяти/документации. Источник истины по полному графику купонов/амортизации/оферт — в любом
случае MOEX ISS (часть A), независимо от того, что отдаёт T-Invest.

## Клиент и сервисы

`InvestApiClient` (создаётся через `InvestApiClientFactory.Create(InvestApiSettings)` или
DI-метод `services.AddInvestApiClient((sp, settings) => {...})`) выставляет gRPC-клиенты как
свойства — это сырые сгенерированные protobuf-клиенты, а не "дружественные" SDK-обёртки:

- `Operations` (`OperationsService.OperationsServiceClient`) — портфель, операции.
- `Instruments` (`InstrumentsService.InstrumentsServiceClient`) — справочник инструментов, купоны.
- `MarketData` (`MarketDataService.MarketDataServiceClient`) — текущие цены, стакан.
- `Users`, `Sandbox`, `Orders`, `StopOrders`, `MarketDataStream`, `OrdersStream`, `History` — не
  используются синком (read-only синк позиций/операций — spec §11). Торговые сервисы (заявки) с задачи 65 вызывает ТОЛЬКО
  `Bonds.Infrastructure/Execution/TInvestOrderGateway.cs` по торговому токену; контракт заявок (сверен отражением 0.6.22.1):
  `PostOrderRequest { AccountId, InstrumentId, Quantity (лоты), Price: Quotation, PriceType (Point|Currency), Direction, OrderType.Limit, OrderId (наш UUID — идемпотентность), TimeInForce }`,
  ответ/`OrderState` — `OrderId` (брокера), `OrderRequestId` (наш), `ExecutionReportStatus`, `LotsRequested/LotsExecuted`, `ExecutedOrderPrice` (средняя за 1 инструмент, MoneyValue),
  `ExecutedCommission`; `GetOrderState`/`CancelOrder` принимают `OrderIdType.Request` (поиск по нашему id); `GetTradingStatus` — `TradingStatus`, `LimitOrderAvailableFlag`, `ApiTradeAvailableFlag`;
  `Users.GetAccounts` — `Account.AccessLevel` (`AccountAccessLevelFullAccess`); `Operations.GetPositions` — `Money` (свободно) / `Blocked` / `Securities[].Balance` (штуки).

Нет отдельного "PortfolioService" — портфель и операции живут в одном `OperationsService`.

## Позиции портфеля — `Operations.GetPortfolio(PortfolioRequest)`

`PortfolioResponse.Positions` → `PortfolioPosition`:

| Поле | Тип | Назначение |
|---|---|---|
| `Figi`, `InstrumentUid` | string | идентификаторы инструмента |
| `InstrumentType` | **string** (не enum!) | нижний регистр, например `"bond"` — фильтр на облигации делается строковым сравнением |
| `Quantity`, `QuantityLots` | `Quotation` | количество (штуки/лоты) |
| `AveragePositionPrice` | `MoneyValue` | средневзвешенная цена покупки (cost basis) — то, что нужно для `Position.AvgPurchasePrice` |
| `CurrentPrice` | `MoneyValue` | текущая цена брокера — первичный источник "сейчас" (plan/00 §4) |
| `CurrentNkd` | `MoneyValue` | **подтверждено**: T-Invest отдаёт текущий НКД по позиции прямо в портфеле — не нужно считать вручную для открытых позиций |
| `ExpectedYield`, `DailyYield` | — | не используются (своя аналитика, этап 05/06) |

`MoneyValue`/`Quotation` кодируют число как `Units` (целая часть, `int64`) + `Nano` (дробная часть
в нанодолях, `int32`, 10⁻⁹) — это формат protobuf-контракта целиком, без отдельного helper-метода
в SDK для конвертации в `decimal`. Реализован в `TInvestNumeric.ToDecimal()`/`ToNullableDecimal()`.

## Журнал операций — `Operations.GetOperationsByCursor(GetOperationsByCursorRequest)`

Предпочтён над более старым `GetOperations(OperationsRequest)` (тоже существует в контракте, без
курсора) — `GetOperationsByCursor` поддерживает:
- `From`/`To` (`Timestamp`) — диапазон по дате, нужен для инкрементального синка (Часть B п.6):
  при повторном синке можно передавать `From = последняя_известная_дата`, а не тащить всю историю.
- `Cursor`/`HasNext`/`NextCursor` (в `GetOperationsByCursorResponse`) — постраничный обход.
- `Limit` — размер страницы (используем 1000).

`GetOperationsByCursorResponse.Items` → `OperationItem` (это **не** старый `Operation` тип, у них
разные наборы полей — контракт изменился между версиями API, важно не путать):

| Поле | Тип | Назначение |
|---|---|---|
| `Id` | string | **используется как `Operation.ExternalId`** для идемпотентного upsert |
| `Type` | `OperationType` enum | `Buy`, `Sell`, `Coupon`, `BondRepayment` (погашение тела), `BondTax`, `BrokerFee`, и др. — большой enum (~50 значений), маппинг на доменный `OperationType` см. `TInvestOperationMapper` |
| `State` | `OperationState` enum | `Executed`/`Progress`/`Canceled` — в журнал берём только `Executed` |
| `Payment` | `MoneyValue` | сумма операции со знаком (потоки) |
| `AccruedInt` | `MoneyValue` | НКД, относящийся к операции (покупка/продажа с учётом НКД) |
| `Date` | `Timestamp` | дата/время операции |
| `Figi`, `InstrumentUid` | string | привязка к инструменту (может быть пустой строкой для операций без инструмента — комиссия по счёту и т.п.) |
| `Quantity`, `QuantityDone`, `QuantityRest` | int64 | количество по операции |

**НКД операций (задача 52).** Допущение: `Payment` покупки/продажи облигации ВКЛЮЧАЕТ НКД (сумма = чистая цена × кол-во + НКД). `AccruedInt` пишется в `operations.accrued_rub` как величина (`Math.Abs`, РУБЛИ на всю операцию, ≥ 0) только для Buy/Sell; если `accrued_rub ≥ |amount_rub|`, калькулятор доходов считает НКД неизвестным и не вычитает его.

**Важно про налог.** `OperationType.BondTax`/`BondTaxProgressive`/`DividendTax` и др. — это
**фактически удержанный** брокером налог (T-Invest как налоговый агент), а не проектируемый.
Это соответствует spec §4.1/часть B п.3: "удержанный налог в журнале — фактический, идёт в XIRR".
Эту величину **не путать** с расчётным НДФЛ 13% на будущие купоны, который считает калькулятор
проекций денежного потока (этап 06) — разные сущности, разный источник правды.

## Справочник по облигации — `Instruments.BondBy(InstrumentRequest)`

`InstrumentRequest { IdType = InstrumentIdType.Figi, Id = figi }` → `BondResponse.Instrument` (`Bond`):

**Подтверждённый факт контракта (ключевой для §12.2):** `Bond` несёт ТОЛЬКО флаги, не полные графики:

| Поле | Тип | Комментарий |
|---|---|---|
| `AmortizationFlag` | bool | **только флаг** наличия амортизации — нет ни одного поля с датами/суммами частичных погашений |
| `FloatingCouponFlag` | bool | флаг плавающего купона |
| `CallDate` | `Timestamp` | **единственное** поле даты оферты/колла — одна дата, не список; **нет поля `PutDate`/PutOptionDate вообще** |
| `MaturityDate` | `Timestamp` | дата погашения |
| `CouponQuantityPerYear` | int32 | количество купонов в год |
| `Nominal`, `InitialNominal` | `MoneyValue` | текущий/первоначальный номинал — наличие двух разных полей подтверждает, что текущий номинал уменьшается при амортизации, но без графика когда/насколько |
| `Isin`, `Figi`, `Uid` | string | идентификаторы |

Это напрямую подтверждает спеку (§4.1, §4.4, plan/04 Часть B п.5): **полный график амортизации и
оферт (особенно put-оферт) у T-Invest не получить** — нет соответствующих полей в контракте.
Источник истины — MOEX `bondization` (часть A). Коннектор T-Invest в этом проекте **не вызывает**
`BondBy` для получения расписаний; он используется только для резолва ISIN по FIGI
(`GetIsinByFigiAsync` в `ITInvestPortfolioClient`), после чего MOEX отдаёт полные графики.

## Купоны — `Instruments.GetBondCoupons(GetBondCouponsRequest)`

Существует и отдаёт `Coupon` { `CouponDate`, `CouponEndDate`, `CouponStartDate`, `CouponNumber`,
`CouponPeriod`, `CouponType`, `FixDate`, `PayOneBond`, `Figi` } — это даёт график купонов (в т.ч.
известные будущие купоны фиксированной бумаги). **Решение этого этапа:** не использовать этот
эндпоинт как источник истины по купонам в синке — MOEX `bondization` остаётся единственным
источником купонного графика в БД (`coupon_schedules`), чтобь не иметь двух конкурирующих
источников истины по одной и той же сущности (plan/04 преамбула: "источник истины по расписаниям —
всегда MOEX"). `GetBondCoupons` оставлен как точка расширения (например, для сверки/будущего
fallback), но не вызывается из `BondSyncService` на этом этапе.

## События по облигации — `Instruments.GetBondEvents(GetBondEventsRequest)`

Существует, `EventType` enum = `Unspecified/Cpn/Call/Mty/Conv` (купон/колл/погашение/конвертация).
Это более новый/специализированный эндпоинт, по которому нет публичных гарантий полноты покрытия
по всем выпускам (в отличие от MOEX ISS, который покрывает весь рынок). **Решение:** не используем
в этом этапе по той же причине, что и `GetBondCoupons` — MOEX остаётся единственным источником
расписаний. Зафиксировано как точка расширения.

## Текущие цены/стакан — `MarketData.GetLastPrices` / `MarketData.GetOrderBook`

`GetLastPricesRequest { Figi = [...] }` → `LastPrice { Figi, Price (Quotation), Time }`.
`GetOrderBookRequest { Figi, Depth }` → `GetOrderBookResponse { Bids, Asks }` (список `Order { Price, Quantity }`,
лучшая цена — первый элемент). Используются как fallback/дополнение к `PortfolioPosition.CurrentPrice`
для расчёта "стакана"/ликвидности — в `ITInvestPortfolioClient.GetQuotesAsync`.

## Счета — `Users.GetAccounts(GetAccountsRequest)`

`GetAccountsResponse.Accounts` → `Account { Id, Name, Status (AccountStatus), Type, OpenedDate }`.
MVP берёт первый счёт со `Status == AccountStatus.Open` (spec §2 — один счёт). Sandbox-режим
(`InvestApiSettings.Sandbox = true`) использует отдельный `Sandbox.GetSandboxAccounts` — не
задействован в синке этого этапа (нужен только для будущей ручной проверки с реальным sandbox-токеном,
см. план "Live-тесты вне CI").

## Не верифицировано / не используется (вне скоупа этого этапа)

- `History`, `OrdersStream`, `MarketDataStream`, `StopOrders`, `Orders` — не нужны read-only синку.
- Реальный сетевой вызов с токеном пользователя — **не выполнялся** (нет токена и не должно быть в
  CI/коде, plan/00 §7 "T-Invest токен... вводится пользователем"). Все факты выше — из самого
  protobuf-контракта (имена методов/полей и их типы в собранной сборке SDK), не из ответа реального
  API. Финальная сверка с реальными данными — на live-smoke тесте, помеченном `Category=Live`,
  который запускается вручную владельцем с его токеном (вне CI, см. `plan/04` "Тесты").

## Операции: тип инструмента и переводы бумаг (plan/39)

- `TInvestOperation.InstrumentKind` — имя enum `Tinkoff.InvestApi.V1.InstrumentType` из `OperationItem.InstrumentKind`
  (`Bond`/`Share`/`Currency`/`Etf`/...; сверено рефлексией по SDK 0.6.22; при `Unspecified` берётся строковое
  `InstrumentType`). Синк сохраняет в журнал только облигационные операции (или операции без типа инструмента).
- `InputSecurities` / `OutputSecurities` (имена enum сверены по SDK) -> `SecurityIn` / `SecurityOut`.


## risk_level облигаций — `Instruments.Bonds(InstrumentsRequest)` (задача 46)

`GetBondRiskLevelsAsync`: ОДИН bulk-вызов `Instruments.BondsAsync(new InstrumentsRequest { InstrumentStatus = Base })`
→ `Bond.risk_level` (instruments.proto:502, enum `RiskLevel`: UNSPECIFIED=0, LOW=1, MODERATE=2, HIGH=3; SDK 0.6.22.1
сверен компиляцией — `RiskLevel.Low/Moderate/High`, поле `Bond.RiskLevel`). Пары (ISIN, уровень) кладутся в
`instrument_risk_levels` раз в сутки шагом `SyncCycleService` (`InstrumentRiskLevelRefresher`, ворота по
`MAX(fetched_at)` ≥ 20 ч). Потребитель — компонент `broker_risk_level` скоркарты надёжности. Реальный вызов с токеном
в CI не выполняется (мок интерфейса).


## Фонды (БПИФ/ETF) — отдельный домен (задача 51)

Фонды не смешиваются с облигациями: `GetPortfolio` кладёт позиции с `InstrumentType == "etf"` в `TInvestPortfolio.Funds`
(облигации — `Bonds`, акции/валюта по-прежнему пропускаются); операции с `InstrumentKind == "Etf"` (`TInvestOperation.IsFund`)
уходят в `FundSyncService`, а не в облигационный журнал.

| Величина | Источник | Единица / конверсия |
|---|---|---|
| `CurrentPrice`, `AveragePositionPrice` позиции фонда | `GetPortfolio` | **рубли за пай**, конверсии нет (`CurrentNkd` у фонда не используется) |
| `Payment` операции по фонду | `GetOperationsByCursor` | рубли, знак брокера как есть (покупка −, выплата +) |
| `Dividend.DividendNet` | `Instruments.GetDividends` | **рубли за пай**, конверсии нет; `RecordDate`/`PaymentDate` — `Timestamp` UTC → `DateOnly` |
| `AssetEtf.TotalExpense` (TER) | `Instruments.GetAssetBy(asset_uid)` → `Asset.Security.Etf` | в SDK-доке «в процентах», на практике не подтверждено → `TInvestNumeric.ExpenseRatioToFraction`: `> 0.05` проценты (`/100`), `0 < raw <= 0.05` уже доля, `<= 0` или `> 10` — null + LogWarning. Хранится **ДОЛЕЙ** (0.009 = 0.9%) |
| MOEX `CLOSE` пая (доска TQBR рынка shares) | `GetSharesHistoryAsync` | **рубли за пай** (у облигаций это % номинала!) |
| MOEX индекс `YIELD` / `DURATION` | `GetIndexHistoryAsync` (RGBI) | проценты → доля (÷100); дни → годы (÷365) |

Справочник фонда: `Instruments.EtfBy(FIGI)` → `Etf.Isin/Ticker/Name/FocusType/AssetUid`; затем `GetAssetBy(AssetUid)` →
`Asset.Security.Etf.TotalExpense` и `Asset.Brand.Company` (управляющая компания). Сбой второго вызова — TER = null, не ошибка.

Имена `OperationType`, сверенные по SDK 0.6.22.1 (`TInvestFundOperationMapper`): `Buy/BuyCard/BuyMargin` -> Buy;
`Sell/SellCard/SellMargin` -> Sell; `Dividend/DividendTransfer/DivExt` -> Distribution (выплата фонда);
`BondTax*/Tax*/TaxCorrection*/DividendTax*/BenefitTax*` -> Tax; `BrokerFee/ServiceFee/MarginFee/CashFee/OutFee/OtherFee/AdviceFee/
SuccessFee/TrackMfee/TrackPfee/OutStampDuty` -> Fee; `InputSecurities/OutputSecurities` -> SecurityIn/Out; прочее — пропуск.

## Валюта (юани и т.п.) — отдельный домен (задача 55)

Имена сверены по `Tinkoff.InvestApi.xml` SDK 0.6.22.1: `Instruments.CurrencyBy` → `Currency` (`IsoCurrencyName`, `Ticker`, `Name`, `Lot`,
`Nominal`), `PortfolioPosition.InstrumentType == "currency"`, `OperationItem.Payment`/`Price` (`MoneyValue` со СВОЕЙ `Currency`).

| Что | Единицы |
|---|---|
| `PortfolioPosition.Quantity` валютной позиции | **ЕДИНИЦЫ валюты** («в штуках»), НЕ лоты (`QuantityLots` не используется) |
| `PortfolioPosition.CurrentPrice` / `AveragePositionPrice` | **РУБЛИ за 1 единицу** валюты (запрос портфеля идёт с `Currency=Rub`), конверсии нет. `AveragePositionPrice` может быть 0/пуст, если брокер не знает цену входа → `avg_purchase_price_rub` NULL |
| `OperationItem.Quantity` сделки с валютой | единицы инструмента по доке SDK; для валюты **не подтверждено на реальных данных** (единицы vs лоты) — берётся как есть, НО кросс-проверяется по цене (`CurrencyOperationUnits.Resolve`): если `|quantity × UnitPrice − |Payment||` > 5% от `|Payment|`, quantity считается лотами и берётся `|Payment| ÷ UnitPrice` (LogWarning о расхождении); без цены — как есть. Расхождение «остаток по журналу ≠ позиция» выводится флагом `hasUnknownLots` |
| `OperationItem.Payment` | сумма **в валюте `Payment.Currency`** (ISO, нижний регистр). Не-рублёвый платёж больше не пишется как рубли: пересчёт по курсу дня из `currency_rate_history` (`PaymentCurrencyConverter`) либо операция пропускается: НЕ ошибка цикла — LogWarning + `SyncResult.SkippedForeignOperations` (в `GET /api/sync/status`). Нулевой платёж не конвертируется (0 ₽). Самолечащийся пропуск (валюта в справочнике, истории курса ещё нет) дополнительно оставляет `JournalSynced=false` |
| `GetPortfolio.TotalAmountCurrencies` (`accounts.cash_rub`) | ВСЕ валюты в рублях — **включая** валютные позиции; валюту нельзя прибавлять к нему повторно |

Рубль (`RUB000UTSTOM`, ISO `rub`) в валютные позиции не попадает. Тип «проценты на остаток» (`OverIncome`/`Overnight`) в Income не мапится —
не относится к валютному инструменту однозначно (см. `TInvestCurrencyOperationMapper`).
