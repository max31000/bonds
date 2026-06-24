# Этап 03 — Доменная модель и хранилище

> **Зависит от:** этапы 01–02.
> **Эталон:** `cashpulse` — `Core/Models/*`, `Core/Interfaces/Repositories/*`, `Infrastructure/Repositories/*`, `Infrastructure/MigrationRunner.cs`, `Infrastructure/DapperTypeHandlers.cs`, миграции `Migrations/*.sql` (EmbeddedResource).
> **Опора по требованиям:** §5 (доменная модель), §4.4 (риски полноты данных), §11 (валюта RUB, устойчивость к неполным данным).

## Цель этапа

Создать слой хранения: SQL-схему (миграции) и Dapper-репозитории для всех сущностей §5. После этапа БД `bonds` содержит все таблицы, репозитории умеют CRUD/выборки, интеграционные тесты гоняют round-trip на реальной MySQL. Доменной математики и коннекторов ещё нет — только модели и хранилище.

> Ключевой разрез (§5): разделять **справочные данные инструмента** (общие), **рыночные котировки** (временной ряд) и **позицию** (cost basis пользователя). Не смешивать в одну таблицу.

---

## Часть A — Доменные модели (`Bonds.Core/Models`)

Реализовать классы/record'ы по §5. Поля — минимально достаточные; типы денег — `decimal`; даты — `DateOnly`/`DateTime` осознанно; валюта по умолчанию RUB.

| Модель | Ключевые поля (не исчерпывающе) | Заметки |
|---|---|---|
| `Instrument` | `Isin` (ключ маппинга), `Secid` (MOEX), `Figi` (T-Invest), `Issuer`, `Sector`, `FaceValue`, `Currency`, `CouponType` (Fixed/Floating/Indexed), `HasAmortization`, `HasOffers`, `MaturityDate` | справочник выпуска |
| `CouponSchedule` | `InstrumentId`, `CouponDate`, `ValueRub`, `IsKnown` | для флоатера известен только до ближайшего пересчёта (`IsKnown=false` дальше) |
| `AmortizationSchedule` | `InstrumentId`, `Date`, `AmountRub` (или доля) | убывающий номинал |
| `OfferSchedule` | `InstrumentId`, `Date`, `OfferType` (Put/Call), `IsExecuted` | put требует действия инвестора |
| `MarketQuote` | `InstrumentId`, `AsOf`, `CleanPrice`, `DirtyPrice`, `Accrued (НКД)`, `Volume`, снимок метрик, `Source` (TInvest/Moex) | временной ряд. Текущая цена/НКД — из T-Invest; историческое/справочное — из MOEX (см. принцип источников в `00`) |
| `YieldCurveSnapshot` | `AsOf`, параметры Нельсона-Сигеля-Свенссона: `B1,B2,B3,T1,G1..G9` | для G-спреда и истории |
| `Position` | `AccountId`, `InstrumentId`, `Quantity`, `AvgPurchasePrice` (cost basis), `Accrued`, `UpdatedAt` | холдинг пользователя |
| `Operation` | `AccountId`, `InstrumentId?`, `Type` (Buy/Sell/Coupon/Amortization/Redemption/Tax/Fee), `Date`, `AmountRub`, `Quantity?`, `ExternalId` | журнал, истина для XIRR; `ExternalId` для идемпотентного синка |
| `ProjectedCashFlow` | `PositionId`/`InstrumentId`, `Date`, `FlowType` (Coupon/Amortization/Redemption), `GrossRub`, `TaxRub`, `NetRub`, `IsEstimated` | производная (заполняется этапом 06) |
| `Account` / `Portfolio` | `Id`, `UserId`, `BrokerAccountId` | один счёт на MVP |
| `PortfolioValueSnapshot` | `AccountId`, `AsOf`, `MarketValueRub`, `XirrToDate`, `InvestedRub` | **ежедневный снимок стоимости/доходности портфеля** для «кривой доходности портфеля во времени» (§9). Заполняется планировщиком (этап 07) |
| `Signal` | `Type`, `Severity`, `PositionId?`, `InstrumentId?`, `SuggestedAction`, `Date`, `IsRead` | заполняется этапом 07 |
| `TargetAllocation` (опц.) | целевые доли, лимиты концентрации | под триггеры/ребаланс |
| `User` | `Id`, `TelegramId`, `Username`, `BaseCurrency`(RUB), `CreatedAt` | расширить таблицу из этапа 02 |
| `DataQualityFlag` (или поле в моделях) | признак «данные неполные» по инструменту/позиции | §4.4 — не падать, помечать |

Интерфейсы репозиториев — в `Bonds.Core/Interfaces/Repositories` (`IInstrumentRepository`, `ICouponScheduleRepository`, …, `IPositionRepository`, `IOperationRepository`, `ISignalRepository`, `IYieldCurveRepository`, `IMarketQuoteRepository`, `IUserRepository`).

## Часть B — Миграции (`Bonds.Infrastructure/Migrations/*.sql`)

- Файлы с числовым префиксом по порядку (`001_init.sql`, `002_...`), как EmbeddedResource; `MigrationRunner` применяет идемпотентно и ведёт реестр применённых.
- Схема MySQL 8.0, `utf8mb4`. Денежные — `DECIMAL(18,6)`. Внешние ключи `instrument_id`/`position_id`. Индексы:
  - `instruments(isin)` unique, `instruments(secid)`, `instruments(figi)`.
  - `coupon_schedules(instrument_id, coupon_date)`, аналогично amortization/offer.
  - `market_quotes(instrument_id, as_of)` — временной ряд.
  - `operations(account_id, date)`, `operations(external_id)` unique (идемпотентность синка).
  - `yield_curve_snapshots(as_of)` unique.
  - `portfolio_value_snapshots(account_id, as_of)` unique — история NAV/XIRR.
- Учесть §4.4: поля под «неполные данные» (напр. `instruments.data_complete TINYINT`, или отдельная таблица флагов).
- **Валюта (§11, RUB-only MVP).** `instruments.currency` хранится, но не-RUB бумаги в MVP **вне скоупа**: помечать флагом `out_of_scope_currency` (или аналогом), исключать из агрегатов/метрик и явно показывать в UI, чтобы не искажать расчёты. Мультивалютность — точка расширения на будущее (см. §11 этапа 00).
- Стартовые справочные значения не требуются (наполняется коннекторами).

## Часть C — Репозитории (`Bonds.Infrastructure/Repositories`)

- Dapper + MySqlConnector. По одному репозиторию на агрегат (паттерн cashpulse).
- `DapperTypeHandlers` для `DateOnly`/enum'ов при необходимости (порт из cashpulse).
- Операции: upsert по `ExternalId` (идемпотентность повторного синка); батч-вставки расписаний.
- Все выборки скоупятся по `UserId`/`AccountId` владельца.
- Регистрация в `Infrastructure/DependencyInjection.cs`.

## Часть D — Тесты (`Bonds.IntegrationTests`)

- `DatabaseFixture` + `TestWebApplicationFactory` (порт из cashpulse) — поднимают тестовую MySQL (контейнер/локальная) и применяют миграции.
- Round-trip тесты: для каждого репозитория — вставка→чтение→обновление; уникальность `isin`/`external_id`; идемпотентный upsert операции (повторная вставка того же `ExternalId` не дублирует).
- Тест миграций: применение с нуля проходит, повторный запуск `MigrationRunner` ничего не ломает.

---

## Критерии приёмки
- [ ] Все модели §5 реализованы в `Bonds.Core/Models` с осознанными типами (decimal для денег, RUB по умолчанию).
- [ ] Миграции применяются на чистую БД без ошибок; повторный запуск идемпотентен.
- [ ] Реализованы и зарегистрированы все репозитории; выборки скоупятся по владельцу.
- [ ] Upsert операции по `ExternalId` идемпотентен (нет дублей при повторном синке).
- [ ] Заложен механизм пометки «неполные данные» (§4.4).
- [ ] Интеграционные round-trip тесты зелёные.

## Проверка
```bash
dotnet test tests/Bonds.IntegrationTests/Bonds.IntegrationTests.csproj
# проверка идемпотентности миграций — повторный запуск контейнера/приложения без ошибок
```

## Definition of Done
БД `bonds` имеет полную схему §5, репозитории покрыты интеграционными тестами, идемпотентность синка и устойчивость к повторным миграциям доказаны запуском. Готова основа для коннекторов (этап 04).

### Дальше → [`04-external-data-connectors.md`](04-external-data-connectors.md)
