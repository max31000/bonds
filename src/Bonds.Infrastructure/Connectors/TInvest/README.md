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
  используются на этом этапе (read-only синк позиций/операций, без торговли — spec §11).

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
