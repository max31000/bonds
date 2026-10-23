# CODEBASE-GUIDE — как устроен этот репозиторий

> Для агентов-исполнителей и людей. Цель — НЕ переоткрывать паттерны при каждой задаче.
> Прочитай перед реализацией; при конфликте с фактическим кодом истина — код (и почини гайд).

## Стек (кратко)

Backend: .NET 8, minimal API, Clean (`Bonds.Api` / `Bonds.Core` / `Bonds.Infrastructure`),
**Dapper + MySqlConnector** (НЕ EF Core), MySQL 8. Frontend: React 19 + TS strict + Mantine 9 +
Recharts 3 + Zustand, сборка Vite, менеджер — **yarn** (не npm!). Тесты: xUnit (+ Testcontainers
MySQL для интеграционных), vitest + Testing Library + MSW.

## ⚠️ Контракт единиц измерения (главный класс багов — ловили трижды)

| Величина | Где | Единица |
|---|---|---|
| Доходности, ставки, спреды, веса, доли | весь бэкенд (модели, сервисы, DTO) | **ДОЛИ** (0.12 = 12%; 0.002 = 20 б.п.) |
| Проценты для человека | только фронт-форматтеры (`format.ts`: `formatPercent` ×100, `formatBp` ×10000) | умножение ровно один раз, на фронте |
| Исключения из «долей» | `RateScenarioService.DeltaPercent`, `PortfolioCompositionService.SharePercent` (+их DTO) — уже В ПРОЦЕНТАХ | задокументировано на полях, закреплено тестами; новых исключений не заводить |
| Деньги | везде | `decimal`, рубли за одну бумагу если не сказано иное |
| Дюрация | движок/модели | годы (`decimal`) |
| Горизонт анализа замены | `user_settings.replacement_horizon_years`, DTO `horizonYears`/`effectiveHorizonYears` | годы (`decimal`, 0.5–10) |
| Пороги рыночных событий | `SignalEngineOptions.SpreadWideningThresholdFraction` (0.0030 = 30 б.п.), `PriceDrawdownThresholdFraction` (0.05 = 5%), `user_settings.spread_widening_threshold_fraction`/`price_drawdown_threshold_fraction`, DTO настроек | **ДОЛИ**; Settings-UI вводит б.п./% и конвертирует один раз на границе API |
| Вердикт позиции | `PositionVerdictService.MinReplacementBenefitFraction` (0.005 = 50 б.п. годовых), `ReplacementOption.AnnualizedBenefitFraction` — **ДОЛИ**; `IssuerSharePercentAfterBuy`/`ConcentrationLimitPercent` — **ПРОЦЕНТЫ** (как `SharePercent`) | не смешивать: сравнение долей портфеля идёт в процентах с обеих сторон |
| Какая дюрация | сравнение позиции с банком/корзинами (бакет RV, фильтр «похожая дюрация», `macaulayDurationYears` в `/comparison`) | **Маколей** — банк хранит MOEX DURATION = Маколей; модифицированная — только мера чувствительности (сценарии ставок, PVBP, композиция портфеля) |
| MOEX ISS: YIELD | вход | **проценты** → храним долю (÷100) |
| MOEX ISS: DURATION | вход | **дни** → храним годы (÷365) |
| MOEX ISS: цены (PREVPRICE/LAST/CLOSE) | вход | **% от номинала** → рубли = `pct/100 × FaceValue` |
| MOEX ISS `marketdata_yields`: EFFECTIVEYIELD, YIELDTOOFFER | вход | **проценты** → доля (÷100) |
| MOEX ISS `marketdata_yields`: GSPREADBP, ZSPREADBP | вход | **базисные пункты** → доля (÷10000: 35 б.п. = 0.0035) |
| MOEX ISS карточка `description`: COUPON_BENCHMARK_SPREAD | вход | **проценты** (строка "1.90") → доля (÷100: 0.019), `security_details.coupon_benchmark_spread_fraction` |
| MOEX ISS `securities`: ACCRUEDINT (НКД) | вход | **рубли на одну бумагу** (не %, не на лот) — конверсии нет |
| T-Invest `GetPortfolio` (CurrentPrice/CurrentNkd/AveragePositionPrice) | вход | **рубли** за бумагу — конверсия не нужна |
| T-Invest `GetLastPrices` (marketdata `LastPrice`) | вход | **ПУНКТЫ** (% от номинала) → рубли = `pts/100 × FaceValue` (см. `LiveQuoteConverter`) |
| ГИР БО ФНС (`bo.nalog.gov.ru`): все суммы бухгалтерской отчётности | вход | **ТЫСЯЧИ рублей** → храним РУБЛИ (×1000, `GirBoParser.ThousandsToRubles`), `issuer_financials.*` — рубли |
| Фонды (БПИФ): цена пая — MOEX `CLOSE` (TQBR/shares), T-Invest `GetPortfolio` `CurrentPrice`/`AveragePositionPrice`, `Dividend.DividendNet` | вход | **рубли за пай**, конверсии нет (НЕ путать с облигациями: там MOEX `CLOSE` — % номинала) → `fund_positions.*_rub`, `fund_price_history.close_rub`, `fund_distributions.amount_per_share_rub` |
| Фонды: TER (`AssetEtf.TotalExpense`) | вход | единицы не подтверждены → `TInvestNumeric.ExpenseRatioToFraction` → **ДОЛЯ** (0.009 = 0.9%), `funds.total_expense_fraction` |
| Фонды: индекс MOEX (`YIELD`, `DURATION`), напр. RGBI | вход | `YIELD` **проценты** → доля (÷100), `DURATION` **дни** → годы (÷365) → `market_index_history.yield_fraction/duration_years` |
| Валюта: T-Invest `GetPortfolio` (`Quantity`, `CurrentPrice`, `AveragePositionPrice` позиции с `InstrumentType == "currency"`) | вход | `Quantity` — **ЕДИНИЦЫ валюты** (юани, НЕ лоты); цены — **рубли за 1 единицу**, конверсии нет → `currency_positions.quantity/*_price_rub` |
| Валюта: MOEX ISS CETS (`history/.../currency/.../CETS/securities/{secid}`) `CLOSE` | вход | **рубли за `FACEVALUE` единиц** (CNY — 1, JPY/KZT — 100; FACEVALUE лежит в `securities`, в истории его нет; `CLOSE = 0` = сделок не было) → рубли за 1 единицу = `CLOSE ÷ FACEVALUE` (`CurrencyRateUnits.RubPerUnit`, ровно один раз в `CurrencyRateRefresher`) → `currency_rate_history.close_rub` |
| `OperationItem.Payment` (`MoneyValue.Currency`) | вход | сумма в **СВОЕЙ валюте** (ISO, нижний регистр), не обязательно рубли: не-рублёвый платёж → по курсу дня (`PaymentCurrencyConverter`) либо операция пропускается с ошибкой синка. Поле `TInvestOperation.PaymentRub` — историческое имя, «рубли» оно только после `TInvestOperationCurrency.ToRubles` |
| `OperationItem.Quantity` / `QuantityRest` / `QuantityDone` | вход | `Quantity` — количество в ЗАЯВКЕ (у частично исполненной заявки больше факта, статус всё равно Executed) → в журнал пишется ИСПОЛНЕННОЕ: `QuantityDone` → `Quantity − QuantityRest` → `Quantity` (`TInvestPortfolioClient.ExecutedQuantity`). Баг 2026-10: «продажа 16 ОФЗ» на 3 020 ₽ (исполнено 3) обнуляла пул журнала до погашения |
| MOEX ISS history бумаги с валютным номиналом (`IsOutOfScopeCurrency`) | вход | % номинала × `FaceValue` и `ACCRUEDINT` — в **валюте номинала** → рубли по курсу дня (`ICurrencyRateLookup`) в бэкфилле истории; нет курса — точка пропускается |
| `accounts.cash_rub` (`TotalAmountCurrencies`) | БД | все валюты брокера **в рублях — ВКЛЮЧАЯ валютные позиции**: валюту нельзя прибавлять к нему (двойной счёт), только выделять: `cashRubOnly = max(0, cashRub − currencyValueRub)` |
| `MarketQuote.CleanPrice/DirtyPrice/Accrued`, `intraday_quotes.dirty_price_rub` | БД | всегда рубли за бумагу (контракт в doc-comment модели) |
| `Position.Accrued` | БД | НКД на ОДНУ бумагу (не на позицию) |
| Знак `Operation.AmountRub` | БД | как отдал брокер — НЕ переписывать (см. `PortfolioXirrService.SignedAmount`) |

Правило: каждая конверсия на границе — с doc-comment'ом «из чего во что». Ревьюер обязан
проверить единицы каждого нового поля.

## Backend-паттерны

**Репозиторий** (`Bonds.Infrastructure/Repositories/*`, образец — `InstrumentRepository`):
класс с конструктором `(string connectionString)`, `CreateConnection() => new MySqlConnection(...)`,
`using var conn` в каждом методе. SQL — verbatim-строки, список колонок — `const string
SelectColumns = "col AS PropertyName, ..."`. Upsert — `INSERT ... ON DUPLICATE KEY UPDATE ...,
id = LAST_INSERT_ID(id); SELECT LAST_INSERT_ID();` (трюк возвращает id и при update). Батчевый
upsert — multi-row `INSERT` с плейсхолдерами `@Field{i}` чанками (образец —
`BondUniverseRepository`), значения ТОЛЬКО через DynamicParameters. **Enum-свойства передавать
в Dapper-параметры явной строкой** (`Type = x.Type.ToString()`) — ITypeHandler для параметров
игнорируется (см. `DapperTypeHandlers.cs`).

**DI** (`Bonds.Infrastructure/DependencyInjection.cs`): репозитории —
`AddScoped<IX>(sp => new X(GetConnStr(sp)))`, где `GetConnStr` читает строку **лениво** (важно
для тестов — `WebApplicationFactory` подменяет конфиг после `AddInfrastructure`; любые чтения
конфигурации в DI — только ленивые, та же история была с DataProtection). Options —
`services.Configure<XOptions>(configuration.GetSection("X"))`, дефолты в property-инициализаторах.
HttpClient MOEX — именованный `MoexIssClient.HttpClientName`.

**Эндпоинты** (`Bonds.Api/Endpoints/*`): статический класс `XxxEndpoints` с
`MapXxxEndpoints(this WebApplication app)`, регистрация в `Program.cs`. Авторизация — глобальная
FallbackPolicy (новый эндпоинт защищён по умолчанию; `.AllowAnonymous()` только осознанно).
DTO — `sealed record` с `required`/nullable. Ошибки: кастомные исключения →
`ErrorHandlingMiddleware`, либо 422 через `Results.Json(new { error, type = "ValidationException" },
statusCode: 422)`. Юзер — параметр `ICurrentUser currentUser` → `await currentUser.GetUserIdAsync()`
(локальный `users.id`), счёт — `PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo)`.

Аналитические эндпоинты `/api/analytics/*` разрезаны по темам (маршруты стабильны; регистрация —
в `Program.cs`): `XirrEndpoints` (xirr, xirr/backfill), `PortfolioAnalyticsEndpoints`
(composition, scatter, rate-scenario, trajectory), `ReplacementEndpoints` (comparison,
replacement, relative-value, replacement-candidates), `AllocationEndpoints` (allocation, basket).
DTO этих эндпоинтов — `Endpoints/Dtos/Analytics*Dtos.cs`; общие хелперы (422-ошибка валидации,
риск-сигналы кандидата, фильтр надёжности) — `AnalyticsSupport`. «События» портфеля — маршрут
исторически `/api/signals` (`SignalsEndpoints`): `SignalsEngine` отдаёт ПОЛНЫЙ набор активных
событий на дату (состояние), `SignalSetSynchronizer` сводит его с таблицей `signals` по
`EventKey` (insert/refresh/resolve); лимит концентрации — из `UserSettings`
(`DefaultMaxConcentrationPercent`), индивидуальный лимит по эмитенту — `target_allocations`.
**Рыночные и кредитные события (задача 49).** Три правила `SignalsEngine` поверх позиций Quantity > 0: `SpreadWidening` (Warning:
эффективный G-спред сегодня − МЕДИАНА его же значений за прошлые ≤ 20 торговых дней > порога 30 б.п. (раннее предупреждение; критический `SpreadShock` скоркарты — отдельная, более высокая ступень 50 б.п.), флоатеры/бумаги вне банка
пропускаются), `PriceDrawdown` (Warning: чистая цена ниже максимума за 20 дней > 5%; формула та же, что компонент `price_drawdown`
скоркарты) и `IssuerDefaultEvent` (Critical: у эмитента позиции в `security_details` хотя бы по одному выпуску `has_default`/`has_technical_default`
= 1; `null` = неизвестно — не событие). Ключи: рыночные — `Type|ISIN|ISO-неделя` (событие не мигает день-к-дню, при нормализации
исчезает из набора и закрывается синхронизатором; < 10 дней истории — молчат), кредитное — `Type|emitent_id:default|technical` без даты
(живёт, пока флаг есть). Входы (`SignalEvaluationInput.MarketContexts`/`IssuerDefaults`) собирает БАТЧЕВО `PositionMarketContextBuilder`
(банк по ISIN `GetByIsinsAsync`, история только по нужным secid `GetRecentHistoryForSecidsAsync` — сегодняшняя строка истории в сравнение
не входит, карточки, дефолт-агрегаты) в `SyncCycleService.RunSignalsAsync`; сбой чтения НЕ глотается (пустой набор закрыл бы активные события и
повторил Critical-алерт). Новое Critical-событие попадает в тот же Telegram-алерт цикла, что и put-оферта (только при `ToInsert`).
Пороги — `SignalEngineOptions`, поверх них `user_settings` (миграция 022: `ENUM signals.type` + две nullable-колонки).
**Вердикт позиции (задача 49).** `PositionVerdictService` (Core, чистая функция): приоритет — ConsiderExit (критический флаг скоркарты /
Red / активное `IssuerDefaultEvent`) → Hold при Coverage 0 («мало данных», Confidence Low) → ConsiderReplace (позиция сравнима по доходности,
годовая выгода лучшей замены ≥ 0.005 после налогов и издержек, светофор цели не хуже hold) → BuyMoreCandidate (Green + Value=Cheap +
доля эмитента после условной докупки на 1% портфеля ≤ лимита + нет активных SpreadWidening/PriceDrawdown/тех. дефолта) → Hold. Confidence —
по Coverage скоркарты (< 0.3 Low, < 0.6 Medium, иначе High). Эндпоинт `GET /api/analytics/position-verdicts` (`PositionVerdictEndpoints`)
собирает входы разом: holdings, `ReliabilityBook`, снимок банка, активные события, цели концентрации, настройки, ставка комиссии; лучшая
замена = топ-3 RV-кандидата корзины позиции (`ResolveCheapBasketMembers`, как `mode=rv`) → `SwitchAnalysisService.Compare` v2 с ногой цели из
БАНК-записи (доходность MOEX YIELD, Маколей, полуспред стакана, оферта/погашение) → `SelectBestReplacement`; Red/критические/без данных/
несравнимые позиции замену не считают. Нога hold в расчёте замены — доходность БАНК-записи позиции (`YieldFraction`, одна методика с ногами-целями; YTM движка — только fallback, если позиции нет в банке), поэтому цифры вердикта могут отличаться от ручного `POST /replacement`. Дефолт эмитента — флаг уровня эмитента (`PositionVerdictInput.HasIssuerDefaultEvent`): событие привязано к одной позиции, а вердикт `ConsiderExit` получают ВСЕ позиции того же `emitent_id`. Сбой расчёта одной позиции даёт ей Hold «не удалось оценить» (LogWarning), остальные считаются; группировка эмитентов для концентрации — регистронезависимая, с суммированием. UI: `PositionVerdictBadge` (таблица/карточка), блок «Вердикт» в карточке позиции, ссылка
`/recommendations?positionId=` раскрывает панель подбора замены (позиция вне топ-3 слабых показывается отдельной карточкой).

**Auth** (`Bonds.Api/Auth`): только общий mvv42-auth — `Mvv42Auth.cs` (copy-in SDK, не править),
`BondsAuth.cs`: схема `mvv42` (JWT из cookie `__Host-mvv42_at`, ключи из JWKS `mvv42-auth:8080`),
Fallback/Default policy = `mvv42:app` + владелец (`tg == Telegram:OwnerId`, иначе 403). Локальный
пользователь ищется/создаётся ТОЛЬКО по claim `tg` (`UserProvisioning`, онбординг Account) — `sub`
(id в auth) никогда не используется как локальный id. `ClaimTypes.NameIdentifier` не читать.

**Миграции** (`Bonds.Infrastructure/Migrations/NNN_*.sql`, EmbeddedResource): следующий номер —
`ls` каталога. MySQL-стиль: `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`,
snake_case, выравнивание типов, `UNIQUE KEY uq_*` / `KEY idx_*` / `CONSTRAINT fk_*`. Runner
режет по `;` (комментарии `--` вырезаются перед сплитом — `;` в комментариях уже безопасен).
**Каждая миграция требует обновить `MigrationIdempotencyTests`** (число + список). Связь новых
таблиц с `instruments` — по **ISIN-строке** (как `watchlist_items`), не FK на instrument_id,
если запись может существовать до появления бумаги в справочнике.

**Фоновые задачи**: `BackgroundService` по образцу `LiveQuotesPollingService` /
`BondUniverseRefreshService` — try/catch вокруг тика, ошибка = `LogWarning` + пропуск итерации,
никогда не валить процесс; `IServiceScopeFactory` для scoped-зависимостей; торговые часы MSK
через options.

**Единое правило «стоимости портфеля» (MarketValue).** Стоимость позиции строится ТОЛЬКО в
`PortfolioHoldingsBuilder` (`PortfolioHolding.MarketValueRub`) = **грязная цена × количество**;
цена за бумагу — fallback-цепочка: `MarketQuote.DirtyPrice` → `DirtyPrice`, посчитанная движком
(чистая цена + НКД, где НКД = `quote.Accrued ?? position.Accrued`) → 0 (нет котировки: позиция
не вносит вклад, но не исключается из списка). Потребители не пересчитывают цену сами: события
(`SyncCycleService`), снимок NAV/XIRR (`PortfolioSnapshotService`), эндпоинты. Поверх правила
снимок применяет правило погашенных бумаг: с `Redemption` в журнале — стоимость 0 (приток уже в
XIRR), дата погашения прошла, а выплаты нет — номинал остатка × количество.

**Ключевые расчётные сервисы** (чистые, `Bonds.Core`): движок — `Calculation/*` (YTM, дюрации,
НКД, XIRR, G-спред: интерполяция кривой — `GSpreadCalculator.CurveValue`); портфельные —
`Analytics/*` (cost basis, сравнение/замена `SwitchAnalysisService`, аллокация/корзина, what-if,
налог продажи `SaleTaxEstimator`, RV `RelativeValueService`, бакеты — `DurationBucketClassifier`).
Анализ замены (`SwitchAnalysisService`, задача 48) — компаундирование обеих ног на эффективном горизонте
`h_eff = min(горизонт пользователя, срок до оферты/погашения ЦЕЛИ)`, не меньше 0.25 г.: доходности после НДФЛ 13% (`CouponTaxRate`,
упрощение — ко всей доходности), слиппедж = полуспред стакана банка `LiquidityScoreCalculator.HalfSpreadFraction` (единая формула;
нет bid/offer → 0 + `SlippageUnknown`), налог продажи — `SaleTaxEstimator` внутри выручки, `FV_switch − FV_hold`, срок окупаемости
аналитически, предупреждение `DurationMismatch` (|Маколей| > 1.5 г.). Горизонт — `user_settings.replacement_horizon_years`
(годы, 0.5–10, шаг 0.5, дефолт 2): фронт шлёт его из `useSettingsStore` (не константа), без поля в запросе бэкенд берёт настройку сам.
**Ранжирование кандидатов (задача 50).** `CandidateYieldRanking` (Core): ключ = `min(YTM банка, yield_to_offer) × (1 − НДФЛ)`
(ДОЛЯ; налог — `AfterTaxYield` с единственной ставкой `SaleTaxEstimator.NdflRate`, её же берёт `SwitchAnalysisService.AfterTax`);
основа `yieldBasis`: "offer" (худший сценарий — оферта или `YIELDDATETYPE=OFFER`) / "maturity"; при равенстве — по SECID.
`GET /replacement-candidates?mode=market` ранжирует так, по умолчанию `durationScope=bucket` — бакет дюрации Маколея позиции ±1 соседний
(`DurationBucketClassifier.AreSameOrAdjacent`; у позиции нет дюрации → откат на `all`, эффективный scope — в ответе), `all` = весь рынок.
DTO кандидата: `yieldFraction` (сырая биржевая) + `afterTaxYieldFraction` + `yieldBasis`; ответ: `durationScope`, `positionDurationBucket`,
`afterTaxRateFraction`. Пул аллокации market/recommended (топ-200) отбирается тем же рангом (`TopByRank`); жадный проход внутри пула не менялся.
Фильтр светофора блока 1 — серверный `reliability=` (лимит 20 применяется после фильтра).
Правило сравнимости доходностей: floater/indexed/dataIncomplete — «вне сравнения»
(`YieldComparability.IsComparable`). Порог отклонения спреда (RV-вердикт и ось спреда
светофора) — `SpreadThresholds.ForBasket(P25, P75)` = max(`DeviationFraction` 0.0020 = 20 б.п., `IqrMultiplier` 0.25 × IQR корзины);
вердикт `RelativeValueService.ResolveVerdict` при Confidence Low (откат на «весь рынок») — `InsufficientData`, не Cheap/Rich.
Ликвидность (`LiquidityScoreCalculator`) считается по МЕДИАНЕ оборота за 5 торговых дней из `bond_universe_history`
(`RelativeValueSnapshot.LiquidityTurnoverRub`, fallback — сегодняшний VALTODAY; всегда через `AnalyticsSupport.AssessEntryLiquidity`,
а не с `entry.TurnoverRub` напрямую); отсутствие bid/offer не даёт Low при живом обороте (High требует известного узкого спреда). Ставка комиссии — ТОЛЬКО через
`ICommissionRateProvider` (override → оценка из журнала → дефолт), не константа.

**Цена пая — одна точка:** `IncomeLedgerBuilder.ResolveFundPriceAsync` (`fund_positions.current_price_rub` → последнее закрытие `fund_price_history` → нет цены = стоимость 0 + `dataIncomplete`); `/api/funds`, `/api/income`, `/api/portfolio/summary` берут её оттуда, свою цену не считают.

**Фонды — отдельный домен (задача 51).** БПИФ (TPAY, TOFZ) живут в своих таблицах (`funds`, `fund_positions`, `fund_operations`,
`fund_price_history`, `fund_distributions`, `market_index_history`, миграция 023) и НЕ попадают в `instruments`/`positions`/`operations`
и облигационный движок (YTM, скоркарта, XIRR/снимки, сигналы). `BondSyncService` отдаёт позиции `Funds` и операции `IsFund` в
`IFundSyncService` (`FundSyncService`: резолв по FIGI, справочник раз в сутки, upsert позиций/операций, сверка с защитой «пустой ответ
брокера — удалять со 2-го раза»); сбой фондов не ломает облигационный синк. Суточные рыночные данные (история цен паёв MOEX, выплаты на пай
T-Invest, история RGBI) — `FundMarketDataRefresher`, шаг `SyncCycleService` (ворота по последней дате в БД; цикл неуспешным не помечается).

**Доходы (задача 52).** Единый журнал `Bonds.Core/Income` (`LedgerEntry` = облигационные `operations` + `fund_operations`) и
`PositionIncomeCalculator` — P&L по КАЖДОЙ позиции, включая закрытые/погашенные (набор активов строится из журнала, не из `positions`).
Метод — average cost (как `PositionCostBasisService`, не FIFO; с налоговой отчётностью может расходиться). Правила: Buy — `cleanCost =
|amount| − НКД`, `accruedPaid += НКД`; Sell — `proceedsClean = amount − НКД`, `realized += proceedsClean − avgCost × q` (продажа больше известного
остатка клампится + `HasUnknownLots`); амортизация уменьшает себестоимость (избыток — в realized); погашение — `realized += amount − cost`
(и перекрывает ещё не удалённую сверкой позицию: стоимость 0); `Income` (купон/выплата фонда) и `Tax`/`Fee` — со знаком брокера; закрытая
позиция с «висящей» себестоимостью списывает её в realized-убыток (+ `HasUnknownLots`). `NetCouponIncome = IncomeGross − AccruedPaid +
AccruedReceived + AccruedNow`. **Инвариант (закреплён тестами на каждом эталоне):** `TotalResult = Σ AmountRub(операции актива) + MarketValueClean +
AccruedNow = Realized + Unrealized + NetCouponIncome + Taxes + Fees`. **`operations.accrued_rub`** (миграция 024) — НКД операции, РУБЛИ на всю
операцию, ВЕЛИЧИНА ≥ 0 (направление из типа Buy/Sell; синк пишет `Math.Abs(AccruedInt)`); NULL = не отдан (заполнится ближайшим синком — журнал
перечитывается целиком). Допущение: `Payment` покупки/продажи включает НКД; если `accrued ≥ |amount|` — НКД игнорируется (`AccruedUnknown`).
`IncomeStatisticsService`: итоги all-time по позициям (+ налоги/комиссии «по счёту» — операции без актива — только в итоговом `TotalResult` и
`accountLevel`); **период `[from,to]` (включительно) — ДЕНЕЖНЫЕ СОБЫТИЯ** (купоны/выплаты брутто, налоги, комиссии, realized на дату операции,
НКД уплачен/получен), бумажная прибыль в периоде не считается (`unrealizedIsAllTime`); помесячный ряд `yyyy-MM` с нулевыми месяцами; XIRR портфеля —
потоки активов + «по счёту» + терминал = чистая стоимость + НКД (деньги на счёте/пополнения не участвуют). Сборка входов —
`IncomeLedgerBuilder` (Infrastructure; стоимость облигаций из `PortfolioHoldingsBuilder`: чистая = MV − AccruedPerBond × Qty; публичный
`BuildFundsAsync` — вход для `/api/funds`, задача 53). Эндпоинты: `GET /api/income` и `GET /api/income/{bond|fund}/{assetId}/ledger`
(`IncomeEndpoints`, DTO `IncomeDtos.cs`; доли — ДОЛИ, суммы — рубли; enum — camelCase-строки).
**Неизвестная себестоимость (хотфикс 2026-10-22).** Единицы без известной покупки (куплены до начала истории, введены переводом, валюта, пришедшая не покупкой) результата не дают: выручка их продажи — `UnknownCostProceedsRub`, стоимость такой части остатка — `UnknownCostHoldingsRub`; обе исключены из итога, бумажной прибыли и XIRR (позиции и портфеля). Пул журнала = известные + неизвестные единицы, продажа списывает пропорционально, сверх пула — неизвестная часть. Раньше себестоимость 0 превращала всю выручку в «прибыль» (юани владельца: +217 тыс. ₽ вместо ~10 тыс.).

**Фонды в продукте (задача 53).** Чистые функции `Bonds.Core/Funds`: `FundPerformanceCalculator` (тотал-ретёрн `(P_end + Σ выплат на пай в (start,end]) / P_start − 1`, ДОЛИ;
окна 1/3/6/12м и «с первой покупки»; цена старта — forward fill, нет цены — окно null; годовая `(1+TR)^(365/дни) − 1`) и `FundOutlookService` (оценка ожидаемой
годовой доходности, ДОЛЯ: `Distributions` = Σ выплат за 92 дня × 365/92 / цена; `IndexYield` = YIELD индекса (RGBI) − TER, TER неизвестен → 0 + `TerUnknown`, точка старше 10 дней
используется, но с `IndexStale` и Low; `TrailingReturn` = 12м, иначе окно от первой цены ≥ 90 дней + `ShortHistory`; `Auto` = Distributions при ≥ 2 выплатах за 6 мес. → IndexYield для
ОФЗ-фондов/явного benchmark (нет данных индекса — откат на Trailing, только для Auto) → Trailing; явный метод без данных → null + `unavailableReason`, без тихого фолбэка;
после налога — `AfterTaxYield.Apply`). `FundOverviewBuilder` (Infrastructure) собирает фонды счёта: P&L — `IncomeLedgerBuilder.BuildFundsAsync` (ОДНА методика со страницей «Доходы»),
рынок — `IFundMarketDataRepository`. Эндпоинты `FundsEndpoints`: `GET /api/funds` (только qty > 0), `GET /api/funds/{id}` (и проданные; чужой/неизвестный → 404),
`PUT /api/funds/{id}/settings` (enum/код индекса `^[A-Z0-9]{2,16}$` → 422), `GET /api/portfolio/summary` (облигации по `PortfolioHoldingsBuilder` + фонды `price × qty` +
`accounts.cash_rub`, сверка с `broker_total_rub`: `brokerDiff = broker − total`; XIRR портфеля — `IncomeStatisticsService`, тот же расчёт, что `/api/income`). `FundRowDto.InvestedRub` —
себестоимость ОСТАТКА (average cost), не Σ покупок. Фронт: вкладка «Фонды» на «Позициях» (`?tab=funds`), `/funds/:fundId` (`FundDetail`), `useFundsStore`, KPI «Стоимость портфеля»/«Фонды»
на «Обзоре»; цены паёв/выплаты на пай — `formatRubPrecise` (копейки), остальное `formatRub`.

**Страница «Доходы» (задача 54).** `/income` (`pages/Income.tsx`, компоненты `components/income/*`, стор `useIncomeStore`, чистые функции `utils/incomeDisplay.ts`,
тексты подсказок `components/incomeCopy.ts`). Фильтры — в URL: `?period=all|ytd|12m|YYYY&asset=all|bond|fund&closed=0` (закрытые показываются по умолчанию, `closed=0` скрывает),
`?asset=bond|fund&focus=<assetId>` раскрывает строку (облигация: assetId = `instrumentId`; фонд: `fundId`). Период режет только ДЕНЕЖНЫЕ события (купоны/продажи/налоги/комиссии);
бумажная прибыль, «Вложено», «Итого» и XIRR — всегда all-time (`unrealizedIsAllTime`). `netCouponIncomeRub` = брутто − НКД уплаченный + НКД полученный + набежавший НКД, БЕЗ налогов
(налоги — отдельный KPI). «Водопад» итога — CSS-бары (`IncomeBreakdown`, `buildBreakdown` даёт инвариант итог = Σ компонентов); журнал операций актива грузится лениво и кэшируется
в сторе по `assetKind:assetId`. Строка таблицы — `Table.Tr` с `aria-expanded` (без вложенных кнопок), на мобильной ширине — карточки `div[role=button]`.

**Валюта — отдельный домен (задача 55).** Юани и т.п. живут в своих таблицах (`currencies`, `currency_positions`, `currency_operations`,
`currency_rate_history`, миграция 025) по образцу фондов и НЕ попадают в `instruments`/`positions`/`operations`/облигационный движок. Облигации/фонды
в иностранной валюте вне скоупа. `BondSyncService` отдаёт позиции `Currencies` (кроме рубля `RUB000UTSTOM`) и операции `IsCurrency` в
`ICurrencySyncService` (`CurrencySyncService`: резолв по FIGI через `CurrencyBy`, справочник раз в сутки + FACEVALUE с MOEX, upsert позиций/операций,
сверка с защитой «пустой ответ — удалять со 2-го раза» — общий хелпер `SyncGuards`, словари на домен отдельные); сбой → `Валюта: …` в ошибках, облигации
не страдают. **Валюта платежа:** перед разбором журнала `BondSyncService.NormalizePaymentCurrenciesAsync` пересчитывает не-рублёвые платежи
(`ICurrencyRateLookup`: последнее закрытие истории не старше 10 дней) или пропускает операцию (ОДНА ошибка со счётчиком; журнал читается целиком, подхватится позже);
действует для облигаций, фондов и валюты. История курса — `CurrencyRateRefresher` (шаг `SyncCycleService` рядом с фондами, MOEX CETS, 2 года, ворота по БД,
`LastCompletedSessionDate` общий с фондами). Тип операции «проценты на остаток» (`CurrencyOperationType.Income`) в журнале и отчёте поддержан, но из T-Invest
не мапится (нет уверенности, какой `OperationType` относится к валютной позиции). Доходы: `AssetKind.Currency` → тот же `PositionIncomeCalculator`
(average cost в рублях за единицу, НКД нет; курсовая разница = Unrealized/Realized; остаток журнала ≠ позиции → `HasUnknownLots`, висящая себестоимость НЕ списывается
в убыток, пока позиция держится); `IncomeLedgerBuilder.BuildCurrenciesAsync` + **единая точка курса `ResolveCurrencyPriceAsync`** (`current_price_rub` позиции →
последнее закрытие истории → нет курса = стоимость 0 + `dataIncomplete`); `/api/income?assetKind=currency|all`, `/api/income/currency/{id}/ledger`, процент на остаток
— `currencyIncomeGrossRub` (период/месяц), реализованная курсовая разница — в существующем `realizedPnlRub`. `GET /api/currencies` (`CurrenciesEndpoints`,
`CurrencyOverviewBuilder`: изменение курса 1/3/12 мес. — ДОЛИ, история ≤ года, проданная валюта остаётся). `/api/portfolio/summary`: `currencyValueRub`, `cashRubOnly`,
`currencyDataIncomplete`; `totalRub` не меняется (валюта уже в `cashRub`). XIRR портфеля включает валютные потоки и терминальную стоимость.

**Фонды на «Аналитике» (задача 57).** `funds.duration_override_years` (миграция 026, ГОДЫ, модифицированная) + `FundDurationEstimator` (Core): ручная → иначе индекс фонда
(явный `benchmark_index` ИЛИ ОФЗ-направленность; код — общий `FundOutlookService.ResolveIndexCode`) `D_mod = D_mac/(1+y/2)` (Маколей индекса в ГОДАХ, y — ДОЛЯ той же точки;
старше 10 дней — с `IndexStale`) → иначе null («неизвестна»). Фонды для аналитики собирает `FundOverviewBuilder.BuildExposuresAsync` (цена — только `ResolveFundPriceAsync`, qty > 0).
`PortfolioCompositionService.CalculateWithFunds` (+ разрез `ByAssetClass`) и `RateScenarioService.Compute(holdings, funds, shifts)` — отдельные перегрузки; `Calculate` и
`Compute(holdings, shifts)` не менялись (SignalsEngine/вердикты фондов не видят; фронт `useRecommendationsStore` просит composition с `includeFunds=false`). `GET /api/analytics/composition|rate-scenario?includeFunds=`
по умолчанию true; ответ: `fundsIncluded`, `hasFunds` (показывать переключатель), у сценариев `funds[]` с `durationSource`/`deltaAtPlus100bpRub`. Δ фонда = −ModDur·Δy·MV без выпуклости.
`PUT /api/funds/{id}/settings` перезаписывает настройки целиком: `durationOverrideYears` (0–30, иначе 422) отсутствует/null = сброс — клиент обязан слать текущее значение.

## Тесты

**Юнит** (`tests/Bonds.Tests`, папки по доменам): без сети и без БД. HTTP-клиенты — вложенный
`FakeHandler : HttpMessageHandler` + Moq на `IHttpClientFactory` (образцы —
`Moex*PagingTests`). Реальные JSON-снимки MOEX — `Fixtures/Moex/*.json`.

**Интеграционные** (`tests/Bonds.IntegrationTests`, плоско по эндпоинтам):
`TestWebApplicationFactory` (env=Testing: миграции на старте пропускаются, hosted services
вырезаны, тестовый ES256-ключ вместо JWKS, DataProtection тестовый) + `DatabaseFixture` (Testcontainers MySQL 8, миграции
прогоняются фикстурой) + `[Collection("Integration")]` (общий контейнер). Авторизованный клиент —
`Mvv42TestAuth.Authorize(client, telegramId)` (cookie + Origin; пользователь резолвится по `tg`; в этой
фабрике проверка владельца пропускает любой tg). Auth-края без БД (401 для всех эндпоинтов,
владелец/403, Origin) — `NoDatabaseWebApplicationFactory` + `Mvv42AuthTests` (Docker не нужен). Внешние клиенты — `factory.WithWebHostBuilder(...
RemoveAll<IMoexIssClient>() ...)`. 401-проверки — `[Theory]/[InlineData]` списком маршрутов.
Тесты НЕ полагаются на порядок и на пустую БД (контейнер общий — маркируй сущности
`Guid`-суффиксом).

**Frontend** (`bonds-web/src`, vitest+MSW): дефолтные хендлеры — `test/msw-handlers.ts`;
`matchMedia` замокан в `test/setup.ts`; Mantine `Collapse` — проверять `toBeVisible()` ПОСЛЕ
раскрытия (не «отсутствие в DOM» до). Даты в тестах — ОТНОСИТЕЛЬНО «сегодня», никаких
абсолютных дат (был time-bomb).

## Frontend-паттерны

Страницы — `pages/*`, сторы — Zustand `store/use*Store.ts` (загрузка виджетов страницы —
независимая, отказ одного эндпоинта не роняет остальные), api-клиенты — `api/*.ts` + типы в
`api/types.ts` (**зеркалят бэкенд-DTO буквально**, включая строковые enum-значения — сверяйся с
сериализацией, был кейс `Unknown` vs `None`). UI — **`@mvv42/kit`** (релизный tarball): экран =
`Page` → `MetricGrid`/`Metric` + `Section` (таблицы — `padding="none"`), состояния — `EmptyState`/
`ErrorState`; графики — `@mvv42/kit/charts` (`ChartFrame` с «?»-объяснением из
`components/charts/explanations.ts`, `useChartTheme`, `ChartTooltip`, `chartColors` — без сырых hex и
`--mantine-color-*`). Свои стили — только `src/app.css` (`bonds-*`, на токенах `--mvv-*`).
API — `api/client.ts` поверх `apiJson` кита (cookie, refresh; финальный 401 → `requestLogin`,
403 → экран «Нет доступа»). Форматтеры — `utils/format.ts`. Мобильная адаптация — карточки по
паттерну `PositionCard` + `useMediaQuery`. Роуты — `App.tsx`, каркас — `components/ServiceLayout.tsx`
(`AppLayout` кита, `LoginRequired`, индикатор синка + «Обновить данные», автосинк — не ломать).
Тесты страниц рендерят их в `MantineProvider`; `/auth/api/me` владельца — `OWNER_ME` в MSW.

**UI скоркарты (задача 47).** `ReliabilityScoreCard` — чистый рендер `RiskSignals` (светофор + балл + `coverage` ДОЛЯ→«данных: N%»
+ флаги по словарю `reliabilityCopy.ts` + компоненты по слоям A/B/C + ось Value + дисклеймер `RELIABILITY_DISCLAIMER`). Точка входа —
`ReliabilityDot` с `details`: клик открывает карточку в `Popover` (desktop) / `Drawer` (< 48em); строки без `components`
(скринер) лениво грузят `GET /api/universe/{secid|isin}/reliability` (`useReliability`). Risk и Value — ДВА независимых бейджа
(`ReliabilityDot` + `ValueBadge`; тултип Value: G-спред / `valueMedianFraction` / `valueThresholdFraction` — ДОЛИ, б.п. рисует
`formatBp`). Ручной ввод — `IssuerDataBlock` (рейтинги + отчётность: форма принимает ₽ / тыс. ₽ (дефолт, как в РСБУ) / млн ₽, в API уходят РУБЛИ — конверсия один раз при отправке; ИНН = `emitentInn` карточки скоркарты или ввод
вручную; страница Настроек), после сохранения родитель рефетчит скоркарту (`refreshToken`). Вложенные `<button>` недопустимы:
строка кандидата-замены — `div[role=button]`, а содержимое попапа гасит всплытие клика (React-события идут через портал).

## Продуктовые константы

Single-user, один счёт. Т-Invest токен — read-only секрет: не логировать, не отдавать на фронт,
хранится шифрованным (DataProtection, ключи на volume). Все аналитики — «оценки, не
инвестрекомендации»: дисклеймеры обязательны (`components/Disclaimer.tsx`), формулировки
«кандидат/оценка», не «купите». Двухъярусная архитектура данных: банк (`bond_universe`) —
дешёвая статистика MOEX для ранжирования (биржевые G-/Z-спред, эффективная доходность и НКД —
блок ISS `marketdata_yields`; внутри кода спред банка читать ТОЛЬКО через
`BondUniverseEntry.EffectiveGspreadFraction` = биржевой ?? наше приближение по кривой,
источник — `GspreadSource` "moex"/"approx"; DTO-поле `gspreadApproxFraction` историческое имя,
наполняется эффективным); эмитент выпуска (`emitent_id/inn/title`) — отдельный суточный UPDATE из bulk-справочника ISS
(`UniverseEmitentEnricher`, часовой upsert снимка эти колонки не трогает); карточки бумаг (флаги
`HASDEFAULT`/`HASTECHNICALDEFAULT`, квал-инвесторы, бенчмарк купона) — персистентный кэш
`security_details` (`SecurityDetailsRepository`, наполняет `SecurityDetailsEnricher` по 200 бумаг в сутки:
портфель/watchlist → банк без карточки → старше 30 дней); флаги `null` = «неизвестно», НЕ «нет»; дефолт-история
эмитента — `GetEmitentDefaultHistoryAsync` (потребитель — скоркарта надёжности); отчётность эмитентов (портфель/watchlist → весь банк) — суточный шаг того же `BondUniverseRefreshService`
(`IssuerFinancialsEnricher`, ГИР БО по ИНН из `bond_universe.emitent_inn`, ≤50 ИНН за сутки (портфель/watchlist, затем весь банк — прогрев за ~3–4 недели), пауза 1 с, 30-дневный кэш статусов
`issuer_financials_status`: ok/not_found/hidden/cb/error; хранилище `IssuerFinancialsRepository`, РСБУ соло, годовые, рубли;
потребитель — слой B скоркарты); **светофор надёжности — скоркарта** `ReliabilityScoreService` (Core, чистая функция, задача 46):
три слоя — A структурный (дефолт-история эмитента, листинг, оферта, не-квал, `risk_level` T-Invest — суточный bulk-вызов
`Bonds()` шагом `SyncCycleService`, таблица `instrument_risk_levels`, + ручной рейтинг `issuer_ratings`), B фундаментальный
(последний год `issuer_financials`; Missing при статусе ГИР БО ≠ ok без ручной отчётности; Stale > 20 мес. → Coverage ×0.5;
для гос/муни вместо B бонус `sovereign`), C рыночный (динамика G-спреда к 20-дневной медиане, просадка цены, ликвидность).
Score = набрано/доступно×100, Coverage = доступно/максимум (Red по Score < 35 только при Coverage ≥ 0.3 — «почти нет данных» → Yellow; флоатер: spread_momentum Missing, Value = InsufficientData); **G-спред и перцентиль корзины в риск НЕ входят** — это ось
Value (`valueVerdict` Cheap/Fair/Rich/InsufficientData отдельным полем). Пороги — `ReliabilityScoreOptions` (эвристика,
калибровка — BACKLOG О2). Вход для всего банка собирает батчево и кэширует `ReliabilityScoreProvider` (Singleton, кэш =
`RelativeValueCacheDuration`, `Invalidate()` при ручном вводе); эндпоинты берут `ReliabilityBook` и отдают
`RiskSignalsDto` (score/coverage/components/criticalFlags/warningFlags/valueVerdict) — `AnalyticsSupport.BuildRiskSignalsDto`;
полная карточка — `GET /api/universe/{secid}/reliability`; ручной ввод — `/api/issuers/{inn}/ratings|financials` (РУБЛИ,
`source='manual'` приоритетнее ГИР БО); точный движок — по требованию через
`POST /api/universe/{secid}/materialize` (единый путь обогащения —
`InstrumentEnrichmentService`).

## Проверка перед завершением любой задачи

```bash
scripts/pre-push-check.sh --all   # зеркало обоих CI-пайплайнов + гварды, сводка в конце
```
Интеграционным тестам нужен Docker. Не заявлять «готово» без фактического вывода.
