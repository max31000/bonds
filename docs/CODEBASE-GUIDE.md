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
| MOEX ISS history бумаги с валютным номиналом (`IsOutOfScopeCurrency`) | вход | % номинала × `FaceValue` — в **валюте номинала** → рубли по курсу дня (`ICurrencyRateLookup`) в бэкфилле истории; `ACCINT` — в валюте расчётов РЕЖИМА (`CURRENCYID`: TQCB `SUR` — уже рубли, TQOD `USD` — × курс, `MoexHistoryPricePoint.AccruedIsRub`); нет курса — точка пропускается |
| `accounts.cash_rub` (`TotalAmountCurrencies`) | БД | все валюты брокера **в рублях — ВКЛЮЧАЯ валютные позиции**: валюту нельзя прибавлять к нему (двойной счёт), только выделять: `cashRubOnly = max(0, cashRub − currencyValueRub)` |
| Freedom (Tradernet API v3): `getPositionJson` `bal_price_a`/`mkt_price`/`market_value` | вход | **в ВАЛЮТЕ БУМАГИ** (`curr` позиции), не рубли; `acc[].s` — остаток в своей валюте. Поле `currval` (₽ за единицу валюты) у брокера бывает УСТАРЕВШИМ — **не использовать**, оценка только по курсам bonds (`FreedomParser` его не читает) |
| Freedom `getUserCashFlows` `sumRaw`, `getTradesHistory` `p`/`q`/`summ`/`commission` | вход | числа приходят **строками** (InvariantCulture); `sumRaw` — в своей `currency` СО ЗНАКОМ (+ приход / − расход); комиссия сделки — в `commission_currency`, может отличаться от валюты сделки |
| `FreedomFeeModel` (`Bonds.Core/Fees`): ставки | константы | **ДОЛИ** (0.005 = 0.5%); суммы — USD; Python-эталон `tn/fees.py` хранит ПРОЦЕНТЫ — конверсия ÷100 уже сделана в таблице |
| `broker_positions` / `broker_cash` / `broker_operations` (счёт Freedom, миграция 030) | БД | ВСЕ цены и суммы — в **ВАЛЮТЕ СТРОКИ** (колонка `currency`), НЕ в рублях; `broker_operations.amount` — со знаком (+ приход / − расход). Рубли — только на чтении через `ICurrencyRateLookup` (`BrokerHoldingsBuilder`), нет курса → `*Rub = null` + `dataIncomplete`. Freedom `RUR` нормализуется в `RUB` при записи. DTO `/api/brokers/freedom/*`: поля без суффикса `Rub`/`Usd` — в валюте строки, `unrealizedPnlFraction` — ДОЛЯ |
| T-Invest `OperationItem.Payment` операций `Input`/`Output` (пополнение/вывод) → `account_cash_movements` (миграция 031) | вход → БД | `amount` — в СВОЕЙ валюте СО ЗНАКОМ брокера (+ пополнение / − вывод), без пересчёта; `amount_rub` — рубль как есть, иначе по курсу дня (`ICurrencyRateLookup`), нет курса — `NULL`. Валюта ISO ВЕРХНИМ регистром (RUR → RUB). DTO `/api/cash`: `amount` в валюте, `amountRub` — рубли |
| T-Invest `Bond.MinPriceIncrement` (шаг цены облигации) → `instrument_trading_meta` | вход → БД | брокер отдаёт в **ПУНКТАХ** (% номинала, как цены `GetLastPrices`) → `min_price_increment` = `пункты ÷ 100 × текущий номинал` в **РУБЛЯХ за бумагу** (`BondPriceStep.PointsToRub`: 0.01 пункта при номинале 1000 = 0.1 ₽) — ТОЛЬКО при номинале в RUB; при нерублёвом номинале (напр. 100 USD, шаг 0.0001) — **NULL**. Сырой шаг — в `min_price_increment_points` (все облигации). Сетка цен облигации T-Invest — в пунктах: заявки по облигациям (фаза 2) округлять в пунктах. `Etf.MinPriceIncrement` — рубли за пай, как есть. Freedom — `min_step` котировки в валюте котировки |
| Заявки исполнителя T-Invest (задача 65): `trade_orders.limit_price` + `price_unit`, `PostOrderRequest.Price` | БД → выход | облигации — **ПУНКТЫ** (`price_unit=points`, `PriceType.Point`, сетка `min_price_increment_points`), остальное — валюта за штуку (`currency`, `PriceType.Currency`); `decimal → Quotation` ровно один раз — `TInvestNumeric.ToQuotation` (Units = целая часть, Nano = дробная × 10⁹, тот же знак). `avg_fill_price`/`executed_commission` — MoneyValue брокера в валюте (облигация — рубли за штуку, не пункты); ВНИМАНИЕ: `PostOrderResponse.executed_order_price` — цена ОДНОЙ штуки, а `OrderState.executed_order_price` — ИТОГО (средняя × количество) → `GatewayOrderState.ExecutedAmount`, цена штуки = итог / (лоты × лотность) (`FillPrice.PerPiece`). Дрейфы `Execution:Max*PriceDriftFraction` — ДОЛИ от утверждённого лимита (фонды 0.002, облигации 0.005 — дизайн §5, симметрично) |
| Заявки исполнителя Freedom (задача 66): `putTradeOrder` `limit_price`/`qty`, `trade_orders` с `broker = freedom` | БД → выход | `limit_price` — **ВАЛЮТА БУМАГИ за 1 штуку** (`price_unit=currency`, источник — `TradePlanLine.LimitPrice`), сетка — `min_step` из `instrument_trading_meta.min_price_increment` (покупка вниз, продажа вверх); `qty` — **ШТУКИ** (лот Freedom = 1, иная лотность — отказ до сети); число в теле — JSON-число без хвостовых нулей (`FreedomRequestSigner.BuildBody`, decimal). Дрейф — та же ДОЛЯ 0.002 от утверждённого лимита. `client_order_id` = числовой `user_order_id` (10 цифр). Комиссия — из `getUserCashFlows` (`sumRaw` по модулю, USD) по сделкам заявки, модель — `FreedomFeeModel` (ДОЛИ, USD) по ФАКТИЧЕСКОМУ маршруту из комментария строки комиссии |
| ЦБ РФ `XML_dynamic.asp` (`VunitRate`/`Value`/`Nominal`) | вход | десятичная ЗАПЯТАЯ; `VunitRate` — рубли за 1 единицу (нет — `Value ÷ Nominal`) → `currency_rate_history.close_rub` как есть, второй конверсии нет (`CbrRatesParser`) |
| `MarketQuote.CleanPrice/DirtyPrice/Accrued`, `intraday_quotes.dirty_price_rub` | БД | всегда рубли за бумагу (контракт в doc-comment модели) |
| `Position.Accrued` | БД | НКД на ОДНУ бумагу (не на позицию) |
| Знак `Operation.AmountRub` | БД | как отдал брокер — НЕ переписывать (см. `PortfolioXirrService.SignedAmount`) |
| Корзины капитала (`baskets`/`basket_members`, `/api/baskets`, задача 62): `target_weight`, `band`, `hard_band`, `sub_weight`, `weight`/`target`/`drift`/`shareOfTotal`/`actualSubShare`/`targetsSum` | БД, DTO | **ДОЛИ** (0.15 = 15%; band — абсолютное отклонение доли портфеля); проценты — только фронт (`utils/baskets.ts`: `percentToFraction`/`fractionToPercent`, ввод формы «Капитал» в %, конверсия один раз при отправке). `value`/`deficitToTarget`/`min_trade`/`freeCash` — в **ВАЛЮТЕ ПОРТФЕЛЯ** (rub — рубли, usd — доллары по курсам bonds) |

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
(`DefaultMaxConcentrationPercent`), индивидуальных лимитов нет (таблица `target_allocations` удалена задачей 62, миграция 032).
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

### Секреты пользователя («Подключения», задача 58)

Токены брокеров/ИИ живут в `user_credentials` (миграция 029), доступ ТОЛЬКО через `IUserCredentialStore`
(шифрует DataProtection, по `userId` из `ICurrentUser`). Новый провайдер = запись в
`Bonds.Core/Credentials/CredentialProviders.cs` + `ICredentialValidator` (регистрация в DI) — без миграций и
эндпоинтов; `GET /api/credentials` и `GET /api/capabilities` строятся по реестру. Секреты в ответы API не попадают
никогда (только маска `last4`). Легаси-исключение: `tinvest_read` хранит ГОЛУЮ строку токена с purpose
`Bonds.TInvestToken.v1` (ciphertext скопирован из `user_settings` миграцией). Фронт: `useCredentialsStore`
(+ `capabilities`, грузятся в `ServiceLayout`), секция `CredentialsSection` на странице настроек.

### Freedom-коннектор (задача 59)

`Bonds.Infrastructure/Connectors/Freedom`: `IFreedomApiClient` (только чтение; торговля — фаза 3) для Tradernet API v3.
Клиент без состояния — `FreedomCredentials(PublicKey, PrivateKey)` приходит в каждый вызов (ключи из
`IUserCredentialStore`, провайдер `freedom`). Запрос `POST {BaseUrl}/api/{cmd}`, тело — компактный JSON, подпись
`X-NtApi-Sig = hex(HMAC-SHA256(privateKey, body + timestamp))` (`FreedomRequestSigner`, время — `TimeProvider`).
Ошибка брокера (не-2xx, `errMsg`/`error`) → `FreedomApiException` без ключей. Тариф сравнивать по **id**
(1430 Investor, 1431 Standard): имя в API бывает коротким. Тикеры `*.ITS` для этого типа счёта только котировочные
(торговля отклоняется), торговать нужно `*.US` — маршрут на ITS выбирает брокер. Комиссии — `FreedomFeeModel`
(`max(pct·сумма + perOrder + perUnit·qty, min)`, perOrder один раз на заявку). Секрет в реестре: `publicKey`
(источник маски) + `privateKey`; валидатор делает `GetAllUserTexInfo` и кладёт в meta `accountNumber/tariffId/tariffName`.
Синк в БД — задача 60 (ниже).

### Счёт Freedom в сервисе (задача 60)

`accounts.broker` (миграция 030, `tinvest` по умолчанию | `freedom`; UNIQUE `(user_id, broker, broker_account_id)`). **Основной счёт продукта — только `tinvest`:**
`IAccountRepository.GetPrimaryAccountIdAsync` и `PositionsEndpoints.ResolveAccountIdAsync` фильтруют `broker='tinvest'` (иначе Freedom с меньшим id перехватил бы
«основной»; закреплено регрессионными тестами); счёт Freedom — `ResolveBrokerAccountAsync(currentUser, accounts, Brokers.Freedom)`. Мультиаккаунт T-Invest не делается.
Freedom — отдельный домен `broker_positions` / `broker_cash` / `broker_operations` (по образцу фондов/валюты; облигационный движок, `positions`, `instruments`, `operations`
их не видят). `FreedomSyncService` (Infrastructure/Sync; отдельный шаг `SyncCycleService` ПОСЛЕ T-Invest, сбой — только `LogWarning`, цикл T-Invest неуспешным не
помечается; ручной запуск — тот же `POST /api/sync`): для КАЖДОГО пользователя с валидным секретом `freedom` (`IUserCredentialStore.ListUserIdsWithValidAsync`, ключи нигде
не логируются) `GetUserInfo` → upsert счёта (`broker_account_id` = номер счёта, имя «Freedom») → портфель (позиции + деньги) → сделки (с 2020-01-01 окнами по году, усечённое
окно в 1000 сделок делится пополам) → 500 последних движений денег. Сверка позиций — `SyncGuards` (пустой ответ удаляет со 2-го раза); деньги: нули не хранятся, `acc` пустой —
остатки не трогаем. Маппинг журнала — `FreedomOperationMapper` (чистый): сделка → `trade:{id}` Buy/Sell (amount ∓|summ|, валюта сделки; комиссию сделки в журнал НЕ пишем —
она приходит отдельной строкой `commission_for_trades`), конвертация валют (`instr_nm` вида `EUR/USD`) → Other/`fx` без тикера; движения: `bank`/`card` → Deposit (+) /
Withdrawal (−), `commission_for_trades` → Fee (тикер из комментария `\(Trade \d+ (buy|sell) ([A-Z0-9.\-]+) \)`), `dividend` → Dividend, `tax` → Tax (тикер `(XXX.US)` из
комментария — best effort), прочее → Other с `raw_type`; нулевые суммы пропускаются; `external_id` = `cf:` + sha256(datetime|type|sum|comment)[..32], идентичные строки
одного ответа различаются суффиксом `#2`. Класс актива — `BrokerAssetClassifier` (ETF|UCITS|Fund в названии → `etf`, иначе `stock` — эвристика).
**Курсы.** `CurrencyRateRefresher` ВСЕГДА ведёт USD, EUR, GBP (`CurrencyRateOptions.AlwaysTrackedCurrencies`, заглушки справочника `figi = FX:USD`, заменяются настоящим
FIGI первым синком T-Invest — `CurrencyRepository.UpsertAsync` обновляет и `figi`). Источники (`TrackedCurrencies`, сверено с живыми MOEX ISS и cbr.ru): USD — MOEX CETS
`USD000UTSTOM`; `EUR_RUB__TOM` и `GBPRUB_TOM` существуют, но сделок нет (CLOSE = 0) — EUR/GBP берутся из официального курса ЦБ РФ (`R01239`, `R01035`, `ICbrRatesClient`);
если MOEX ничего не отдал, а код ЦБ известен — курс ЦБ. Оценка в рублях — `BrokerHoldingsBuilder` (только `ICurrencyRateLookup`, RUB = 1; поле `currval` брокера не
используется). API: `GET /api/brokers/freedom/holdings` (404 без счёта Freedom; позиции + `marketValueRub`, деньги + `amountRub`, `totalValueRub/Usd`, `syncedAt`,
`dataIncomplete`, `fees.feesPaidUsd` — Σ комиссий в USD, положительное число), `GET /api/brokers/freedom/operations?from&to`. `/api/portfolio/summary`: `otherBrokersValueRub`
(Σ не-T-Invest счетов, 0 без них), `otherBrokersDataIncomplete`, `capitalTotalRub = totalRub + otherBrokersValueRub`; **`totalRub` не менялся** (сверка и XIRR).
Фронт: вкладка «Freedom» на «Позициях» (`?tab=freedom`, `FreedomTable`, `useFreedomStore`) и KPI «Freedom» (₽ и $) / «Капитал всего» на «Обзоре» — ТОЛЬКО при
`capabilities.freedom` (без неё запросов к `/api/brokers/*` нет; подсказка — в карточке Freedom в «Подключениях»); суммы в валюте бумаги — `formatMoney` с КОДОМ валюты.

### Деньги и торговые метаданные (задача 61)

Данные и чтение для правил фазы 2 (ни правил, ни заявок). **Движения денег:** `BondSyncService` больше не выбрасывает `Input`/`Output` журнала T-Invest —
`TInvestCashMovementMapper` (`Input|InputAcquiring|InputSwift` → Deposit, `Output|OutputAcquiring|OutputSwift` → Withdrawal; переводы бумаг, `InpMulti`/`OutMulti`
и штрафы НЕ деньги капитала) складывает их в `account_cash_movements` (UNIQUE `account_id+external_id` — повторный синк не дублирует; сбой записи — ошибка в
результате, облигационный журнал не страдает). **Торговые метаданные** `instrument_trading_meta` (UNIQUE `broker+instrument_key`, ключ — ISIN, иначе тикер;
общие для пользователей): `TradingMetaRefresher` — необязательный суточный шаг `SyncCycleService` (ворота по `updated_at` самих записей, 20 ч; сбой одной бумаги —
LogWarning, цикл не краснеет). T-Invest: позиции основного счёта (ISIN облигаций и фондов) + `TradingMetaOptions.FundUniverse` (Т-Капитал `TMON@ TSAV TBRU@ TOFZ@
TPAY`, сторонние `LQDT SBFR SBRB OBLG SBGB AKMB`); `ITInvestPortfolioClient.GetInstrumentListingsAsync` отдаёт ВСЕ листинги тикера (`FindInstrument` + `BondBy`/`EtfBy`
по uid), режим торгов — `TradingListingSelector` (api=true → TQBR → по алфавиту; LQDT: TQBR api=false, SPBRU api=true → SPBRU). `pays_distributions` фонда —
`CountDistributionsAsync` (`GetDividends` за 365 дней; сбой запроса = «не знаем», прежнее значение остаётся, а не 0). Freedom: тикеры позиций пользователей с
валидными ключами + `SPYM.US`, `VUAA.EU`; лот 1, шаг — `min_step`, `its_twin` — есть ли «живая» котировка `<base>.ITS` (хоть одно положительное поле; пустая запись
брокера = двойника нет), `*.ITS` — `api_trade_available=false` (только котировки). Единицы шага цены — в таблице выше. **API:** `GET /api/cash` (`CashOverviewBuilder`:
по брокерам пользователя — остатки по валютам, `cashRubOnly` = свободные рубли БЕЗ валюты (T-Invest: `cashRub − валюта`, Freedom: остаток RUB), последние 10 пополнений/
выводов) и `GET /api/trading-meta?broker&keys` (общая справка, ≤ 200 ключей, без `keys` — все записи брокера). Фронт: блок «Деньги на счетах» на «Обзоре»
(`CashOverviewSection`, независимая загрузка `fetchCash`).

### Корзины капитала (задача 62)

Модель целей и расчёт отклонений, **без правил и заявок**. Миграция 032: `baskets` (user_id, `portfolio` rub|usd, `code` UNIQUE в пределах пользователя, цели/полосы — ДОЛИ, `liquidity_tier` 0..3,
`fill_rule` fixed|engine|hold, `auto_buy/sell`, `min_trade` — валюта портфеля) и `basket_members` (broker, `instrument_key` — ISIN/тикер либо класс актива, `sub_weight`, `match_rule`
instrument|asset_class); таблица `target_allocations` **удалена** (лимит эмитента — только `user_settings.default_max_concentration_percent`). Два самостоятельных портфеля: **rub** = T-Invest
(облигации, фонды, рубли, валюта; оценка в рублях) и **usd** = Freedom (оценка в долларах: бумага в USD — как есть, иначе рубли по курсам bonds ÷ курс USD; нет курса → `dataIncomplete`).
Стартовые корзины (`DefaultBaskets`) сидятся при ПЕРВОМ `GET /api/baskets` (данные пользователя, не миграция; гонка двух первых запросов безопасна — UNIQUE user_id+code), `POST /api/baskets/reset-defaults[?portfolio=]` возвращает их.
`BasketDriftService` (Core, чистый): позиция → явный член (брокер + ключ без регистра и хвостового «@»; у члена Freedom без суффикса биржи `LUV` совпадает с `LUV.US`, `VUAA.EU` — только точно) → правило класса
(`asset_class`: bond/fund/currency у T-Invest, etf/stock у Freedom) → системная строка: валюта T-Invest — `fx`, остальное — `unassigned` (обе вне целей). Свободные деньги — строка `cash` (T-Invest: `CashBreakdown.CashRubOnly`, Freedom: Σ остатков в USD).
База доли = портфель − корзины с `target_weight = null` (включая `fx`/`unassigned`); `cash` в базе. `weight` = value ÷ база; `drift` = weight − target; `deficitToTarget` = target × база − value (положительное — недобор);
состояние: |drift| ≤ band — Ok, ≤ hard_band — Below/Above, дальше — HardBelow/HardAbove, без цели — NoTarget, цель есть, но база доли ≤ 0 — NoData (drift/дефицит = null). Стоимость берётся только из существующих точек истины (`PortfolioHoldingsBuilder`, `ResolveFundPriceAsync`,
`ResolveCurrencyPriceAsync`, `BrokerHoldingsBuilder`) в `CapitalOverviewBuilder`. API: `GET /api/baskets`, `PUT /api/baskets/{id}` и пакетный `PUT /api/baskets` (цели нескольких корзин атомарно — иначе 15/85 → 20/80 невозможно),
`POST/DELETE /api/baskets/{id}/members`. Валидация (422): Σ целей портфеля = 1 ± 0.0001 — проверяется на ИТОГОВОМ состоянии и только для портфелей, где цель хоть одной корзины изменилась; band < hard_band; бумага — член ровно одной корзины портфеля;
брокер члена соответствует портфелю. Фронт: страница «Капитал» (`/capital`, `pages/Capital.tsx`, `components/capital/*`, `useBasketsStore`), раздел usd — только при `capabilities.freedom`, дисклеймер «оценка, не рекомендация».

### Движок правил и планы сделок (задача 63) — по умолчанию РЕЖИМ НАБЛЮДЕНИЯ

**Инвариант (переписан задачей 65):** заявки T-Invest отправляет ТОЛЬКО `ITInvestOrderGateway` (`Bonds.Infrastructure/Execution/TInvestOrderGateway.cs`) по команде исполнителя — см. раздел
задачи 65 ниже; заявки Freedom (размещение, снятие, список заявок Tradernet) — ТОЛЬКО `IFreedomOrderGateway` (`Bonds.Infrastructure/Execution/FreedomOrderGateway.cs`, задача 66), стоп-заявки Freedom — нигде.
Закреплено `NoOrderPlacementArchitectureTests` (скан исходников + рефлексия; даже комментарии краснят тест).
Планы — данные: `trade_plans`/`trade_plan_lines` (миграция 033); `dry_run` = НЕ живой режим пользователя на момент сохранения (задача 65), `approve` dry-run плана ставит `Approved` и пометку «не исполнен: dry-run».

**Слои.** `Bonds.Core/Policy`: правила — чистые функции `(PolicyInput) → PolicyOutcome` (`SweepRule` P1, `InflowRule` P2, `LiquidityToBondsRule` P3b, `RebalanceRule` P4, `WithdrawRule` P5, `ExitBasketRule`,
`FreedomTopUpRule` P7 — маршрут ITS/Europe выбирается сравнением реальных комиссий `FreedomFeeModel` обоих маршрутов по тарифу счёта, при равенстве — `CheaperRoute`), общее ядро — `PolicyKit` (цена, лоты, комиссия, статус по лимитам, итоги), `LinePricer` (mid стакана к шагу: покупка ВНИЗ, продажа ВВЕРХ; нет bid/ask — бумага пропущена),
`TradingHours` (будни, MSK = UTC+3: Мосбиржа 10:00–18:40, `SPBRU` как опция, Freedom `.US` 17:30–23:00, `.EU` 11:00–18:30; праздники бирж не учитываются). `Bonds.Infrastructure/Policy`: `PolicyInputBuilder`
(собирает вход из `CapitalOverviewBuilder`, метаданных торгов, стаканов `IPlanQuoteSource` — T-Invest `GetQuotesAsync` (order book глубины 1) и Freedom `GetQuotesAsync`, позиций со средней ценой входа, комиссии),
`PolicyBondCandidateProvider` (кандидаты-облигации), `PolicyService` (предпросмотр, ручные планы, approve/reject), `PolicyRunner` (шаг `SyncCycleService` после синка всех брокеров, сбой — только лог).
`Bonds.Api`: `PlansEndpoints` (`GET /api/plans?status`, `GET /api/plans/{id}`, `POST …/approve|reject` (409 — уже решён или просрочен), `POST /api/plans/inflow|withdraw|exit-basket|preview`).

**Единицы и границы.** Все доли — ДОЛИ (премия P3b `PolicyOptions.LiquidityPremiumFraction` = 0.015; `fraction` вывода = 0.1 для 10% — проценты только в форме фронта через `percentToFraction`); деньги плана — в валюте
ПОРТФЕЛЯ (rub — ₽, usd — $); строка плана — в ВАЛЮТЕ ЦЕНЫ бумаги (`est_amount` с НКД, `est_fee`, `est_tax` неотрицательны, сторона — в `side`); `FxToPortfolio` переводит валюту цены в валюту портфеля.
Облигации T-Invest: стакан и сетка цен — в ПУНКТАХ (`min_price_increment_points`), `limit_price_points` — то, что отправит исполнитель; `limit_price` — чистая цена в рублях за штуку = пункты / 100 × номинал,
номинал восстановлен из метаданных (рублёвый шаг ÷ шаг в пунктах × 100); у облигаций с нерублёвым номиналом (рублёвый шаг NULL) рубли для суммы берутся из последней известной цены (оценка), лимит — всё равно в пунктах.
Количество — целые лоты (`quantity_lots` × `lot_size` = штуки). Комиссия: T-Invest — ставка `ICommissionRateProvider` (ДОЛЯ суммы; фонды УК брокера `PolicyOptions.OwnFunds` — 0), Freedom — `FreedomFeeModel` по тарифу из мета ключей (по умолчанию Standard).
**Единая валюта комиссий и резерва:** `est_amount`/`est_fee`/`est_tax` строки — ВСЕГДА в валюте цены строки (`PlanFeeModel.Estimate` переводит USD-комиссию Freedom в валюту цены делением на `FxToPortfolio`); итоги
`summary_json` (`buyTotal`, `feeTotal`, `reservedBuy`, `cashAfter`) — в валюте портфеля. Резерв денег (`SumReservedBuysAsync`) суммирует `summary.reservedBuy` (покупки + комиссия в валюте портфеля), а не сырые строки.
Санити-проверка стакана (`LinePricer`): бумага пропускается при ask < bid, спреде > `MaxSpreadFraction` (2%, облигации `MaxBondSpreadFraction` 3%) или |mid − last|/last > `MaxLastDeviationFraction` (5%, если last известна).

**Лимиты авто-подтверждения — из конфигурации, не из БД:** `ExecutionLimitsOptions` (`ExecutionLimits__AutoPerOrderRub` 25 000, `AutoPerDayRub` 50 000, `FreedomAutoUsd` 0). `AutoApproved` — только покупки, заявка ≤ лимита
заявки, Σ плана + уже авто-подтверждённое за московские сутки ≤ лимита дня; корзина должна иметь `auto_buy`; на Freedom авто выключено (P7 всегда `PendingApproval`). Планы P2 с облигационной частью, P3b, P4, P5,
выход из корзины и ВСЕ ручные планы — всегда `PendingApproval`. `PendingApproval` и `AutoApproved` живут `PolicyOptions.PendingTtl` (30 мин), но не дольше закрытия сессии рынка строк; просроченные переводит в `Expired`
начало прогона (у AutoApproved в сводке «не исполнен»). `AutoApproved` можно отменить тем же `POST /api/plans/{id}/reject`. Суточный лимит из БД считает только `Submitting`/`Done`: в dry-run AutoApproved не исполняется, поэтому в день не входит;
внутри одного цикла `PolicyRunner.CarryForward` наращивает счётчик по только что сохранённым AutoApproved (иначе два авто-плана подряд обошли бы 50k/день). P1 не паркует кэш в «ликвидность» в состоянии Above/HardAbove.

**Прогон (`PolicyRunner`).** Порядок: P2 по НОВЫМ пополнениям T-Invest (окно `DepositLookback` 3 суток, один план на пополнение навсегда; если плана нет — пополнение не «съедено», повтор в следующем цикле) → P1 (остаток
свободных денег паркуется в «ликвидность» ПОСЛЕ P2, иначе P1 увёл бы всё пополнение) → P7 → P3b (в `LiquidityCheckDay`, по умолчанию понедельник МСК, при состоянии Above/HardAbove и весе > 0.20) → P4 (не при открытом P3b).
Деньги созданных планов резервируются для следующих правил и циклов (`SumReservedBuysAsync`; Approved dry-run деньги НЕ резервирует). P3b/P4 проверяют дубликат ДО сбора облигационных данных. Дубликат не создаётся при открытом плане того же `rule` + `trigger` и в паузу
`RepeatCooldown` (6 ч) после любого плана с тем же `rule` + `trigger` (иначе без исполнителя каждый цикл рождал бы новый). Вне часов торгов автоматические правила плана не строят; ручные запросы и `preview`
игнорируют часы, но помечают итоги `pricesPossiblyStale`.

**Telegram-бот (задача 64).** Тот же бот (`Telegram:BotToken`), long polling `getUpdates` (25 с), webhook нет. `Bonds.Core/Telegram` — чистые функции: `BotCommandParser` («15%» → ДОЛЯ 0.15 один раз), `BotCallback`
(`p:{planId}:{a|r}:{nonce}`, nonce = 8 случайных байт base64url в `trade_plans.bot_nonce`, не уходит в API), `PlanMessageFormatter`/`BotTexts`/`BotFormat` (HTML-экранирование, русская локаль независимо от
культуры сервера, «режим наблюдения: сделка не отправляется»). `Bonds.Infrastructure/Telegram`: `ITelegramBotClient` (sendMessage c inline-клавиатурой, editMessageText, answerCallbackQuery, getUpdates),
`TelegramBotPollingService` (offset в `bot_state`, сбой — `LogWarning` + 5 с; HTTP 409 = второй потребитель getUpdates или webhook → пауза 60 с и ОДИН алерт владельцу через `ITelegramAlertSender` раз в 6 ч;
пустой/плейсхолдерный токен — простой), `TelegramBotUpdateHandler` (пользователь = строка `users` по Telegram id, любой зарегистрированный управляет СВОИМИ планами, остальным «Нет доступа»; команды только в личке;
approve/reject — тот же `PolicyService`, что веб, `decided_via=telegram`), `TelegramPlanNotifier` (`IPlanNotifier`, вызывается `PolicyRunner` после сохранения плана: PendingApproval — с кнопками, AutoApproved — без).
`/stop` → `user_settings.execution_paused` (миграция 034): `PolicyRunner` сохраняет планы с `forcePending` (никаких AutoApproved). Токен — часть пути запроса: логи стандартного HttpClient для `telegram-bot*`
приглушены до Warning (иначе путь с токеном печатается на Information), в исключения бота url не попадает. Кнопка «Детали» — `TelegramBot:WebBaseUrl` + `/bonds/plans/{id}` (пусто — кнопки нет).
В интеграционных тестах клиент заменён `FakeTelegramBotClient`.
Лимиты Telegram: итоговый текст ≤ 4096 символов UTF-16 ПОСЛЕ экранирования — `PlanMessageFormatter.FormatHtml` режет причину/предупреждения/футер (`BotFormat.HtmlTruncated`, футер передаётся СЫРЫМ и экранируется внутри) и строки плана («… и ещё N стр.»); `/plans` показывает `TelegramBot:MaxListedPlans` (10) новейших открытых планов + «… и ещё N — см. веб» (кнопок ≤ 30 при лимите 100); эхо пользовательских токенов в ошибках — ≤ 32 символов (`BotFormat.Echo`); сумма `/inflow`/`/withdraw` ≥ 1 единицы валюты; `TelegramBotClient` — страховочная обрезка (`BotFormat.TruncateHtml`, теги закрываются) с предупреждением в лог. Уведомление о новом плане ограничено `TelegramBot:NotifyTimeout` (5 с) и получает `ct` прогона. `TelegramBot:WebBaseUrl` по умолчанию `https://mvv42.ru` (публичный адрес), переопределяется env `TelegramBot__WebBaseUrl`.
**Известный режим потери (poison update, осознанно не лечится).** `TelegramBotUpdateHandler.HandleAsync` не бросает исключений обработки (кроме отмены), поэтому `TelegramBotPollingService` считает апдейт обработанным и двигает offset (`bot_state`) независимо от результата. Плюс: «ядовитый» апдейт не зацикливает бота. Минус: апдейт, обработка которого упала (например БД недоступна в момент нажатия «Подтвердить»), не повторяется — пользователь увидит только отсутствие реакции и повторит команду/кнопку (решение плана атомарно и идемпотентно, двойное применение невозможно). Ретрай с счётчиком попыток — отдельная задача, если потери станут заметны.

**Исполнитель T-Invest (задача 65) — ВЫКЛЮЧЕН по умолчанию.** Ворота (`Bonds.Core/Execution/ExecutionGate`, живые данные — `ExecutionGateResolver`): заявка возможна, только если ВСЁ: env
`Execution__Enabled=true` (appsettings — false, тест `AppSettings_NeverEnableExecution`), `user_settings.execution_mode = live` (миграция 035, дефолт `observe`), секрет `tinvest_trade` в статусе ok С ОТМЕТКОЙ
сетевой проверки в meta (`TradeCredentialMeta`: `verification=network`, `accessLevel=full`; старые формальные записи миграция 035 перевела в `unverified`), нет паузы `/stop`. Валидатор `tinvest_trade` —
`GetAccounts` по токену: ровно один счёт, `ACCOUNT_ACCESS_LEVEL_FULL_ACCESS`, открыт, = `accounts.broker_account_id` основного счёта (контекст — `ICredentialValidator.ValidateAsync(fields, context)`); в meta только last4.
`PolicyService.SaveAsync` пишет `dry_run = 0` только при открытых воротах ВСЕХ брокеров строк (задача 66 — см. ниже). **Шлюз** `ITInvestOrderGateway` (Scoped, единственный вызыватель торгового сервиса SDK): торговый токен читает сам,
read-only токен не трогает, `PostLimitOrderAsync` ПЕРЕПРОВЕРЯЕТ ворота и отказывает (`gate_closed`); цена → `Quotation` один раз (`TInvestNumeric.ToQuotation`), `PriceType.Point` для облигаций (ПУНКТЫ) и
`PriceType.Currency` для остального; `order_id = client_order_id`. gRPC-ошибки → `TInvestGatewayException` (`Transient` — исход неизвестен, `Rejected` — определённый отказ). **Движок** `PlanExecutionEngine`
(`PlanExecutorService`, тик `Execution:TickInterval` 20 с): по пользователям с `Submitting` или `Approved/AutoApproved` + `dry_run = 0`: ворота → сверка токена у брокера на КАЖДОМ тике (не прошла — секрет `invalid`,
остановка) → не просроченный план в `Submitting` (`SetExecutionStatusAsync`, `decided_*` не трогает) → строки ПРОДАЖИ РАНЬШЕ ПОКУПОК, по одной → `PreflightChecks` (пауза, окно сессии, свежий стакан ≤ `MaxQuoteAge`,
|mid − утверждённый лимит|/лимит ≤ `MaxPriceDriftFraction` 0.002 / облигации `MaxBondPriceDriftFraction` 0.005 — ДОЛИ, в единицах брокера; `NormalTrading` + лимитные заявки; `api_trade_available` брокера И мета
+ сторона; деньги — `GetPositions.money` (уже без заблокированного под отправленные заявки) ≥ `OrderPricing.RequiredMoney`, продажа — остаток бумаги; авто-план — сумма заявки по её цене ≤ `AutoPerOrderRub`
и `AutoDayUsage` (ФАКТ исполненного дня: лоты × лотность × средняя цена + оценка неисполненного, `ListAutoDayPlansAsync`) + план ≤ `AutoPerDayRub`) → строка `trade_orders` со статусом `New` ЗАПИСЫВАЕТСЯ ДО отправки (UNIQUE `client_order_id` и `(plan, line, attempt)`) → отправка. `New` без ответа только сверяется (`GetOrderState` по
`OrderIdType.Request`, затем `GetOrders`), не нашлась за `UnconfirmedOrderTimeout` — `Failed`/`unknown_state`, ПОВТОРНО НЕ ОТПРАВЛЯЕТСЯ. Не исполнена за `RepriceAfter` (5 мин) — снятие и попытка k из `MaxAttempts` (3)
на (k−1)/(N−1) пути к противоположной стороне стакана, но не дальше лимит × (1 ± дрейф) (`OrderPricing.PriceForAttempt`, сетка: покупка вниз, продажа вверх), остаток лотов; затем `Expired`. За `CancelBeforeClose`
(10 мин) до закрытия (`TradingHours` рынка строки) висящие снимаются, новые не отправляются. Ворота закрылись посреди исполнения — висящие снимаются, неначатые строки `Failed`/`halted`. Итог плана —
`Done`/`PartiallyDone`/`Failed` (`PlanExecutionOutcome`), синк портфеля (`IPortfolioSyncTrigger`). Сверка комиссии: факт vs `est_fee` × доля исполненных лотов, |Δ| > `FeeMismatchThreshold` (0.01, валюта строки) —
предупреждение; комиссия 0 сразу после исполнения при ненулевой модели — сверка и уведомление «исполнено» откладываются до `MaxFeeRechecks` (3) тиков с перечитыванием (`trade_orders.fee_rechecks`/`fee_checked_at`,
миграция 036), план закрывается после сверки. Уведомления — `IExecutionNotifier` (Telegram, `ExecutionMessages`, ≤ 4096): отправлено (кнопка «Отменить» = `p:{id}:c:{nonce}` → `cancel_requested`), исполнено (цена, лоты, комиссия), ошибка, снято, итог.
**Доработки по ревью:** снять заявку нельзя (нет токена, токен на чужой счёт, брокер отказал) — ОДНО уведомление «Заявка X ×N не снята автоматически — снимите её в терминале брокера» (`halt_alerted_at`), план
ОСТАЁТСЯ `Submitting` (новый статус не вводился), попытки каждый тик, в API `needsAttention`. Удаление/замена ДРУГИМ `tinvest_trade` при активных заявках — 409 `ActiveOrders`, с `?cancelActive=true`
`TradeTokenChangeGuard` под блокировкой исполнителя снимает их ТЕКУЩИМ токеном (подтверждение `GetOrderState`, код `token_change`, планам — `cancel_requested`) и только потом меняет токен. Тик — под MySQL
`GET_LOCK('bonds-plan-executor', 0)` на отдельном соединении (`IPlanExecutorLock`; не взяли — пропуск). gRPC-клиент — singleton `TInvestTradeClientCache` по (пользователь, SHA-256 токена). Вне часов торгов
подтверждённый живой план ЖДЁТ в `Approved`/`AutoApproved` до `expires_at`; `ApproveAsync` живого плана продлевает `expires_at` до закрытия ближайшей сессии (`TradingHours.NextSessionCloseUtc`, запас
`CancelBeforeClose`, T-Invest = Мосбиржа), заметка — по текущим воротам. `AutoApproved` при старте перепроверяется `AutoExecutionWhitelist` (покупки RUB, не облигации, ≤ `AutoPerOrderRub`, член корзины с `auto_buy`).
Цена не легла в `Quotation` — `Failed`/`price_grid` до сети. Активные заявки (`activeOrders` в `GET /api/plans` и `/api/settings/execution`) — баннер «Есть активные заявки у брокера» в любом режиме.
API: `GET /api/settings/execution`, `PUT /api/settings/execution-mode {mode, confirmation:"LIVE"}` (live — только с проверенным токеном; сообщение в бот), `PUT /api/settings/execution-pause {paused}`,
`POST /api/plans/{id}/cancel-execution` (флаг, 409 — не исполняется); строки `/api/plans` несут `executionStatus`/`filledLots`/`orders[]` (id брокера наружу не отдаётся), `dryRun` списка — ворота пользователя.
Фронт: `ExecutionModeSection` (Настройки, ввод «LIVE»), баннер `DryRunBanner live` + «Остановить» (= пауза), статусы заявок на `PlanCard`. Песочница — `TInvestSandboxTests` (`[Trait("Category","Sandbox")]`,
env `TINVEST_SANDBOX_TOKEN`), исключена фильтром `Category!=Sandbox` в CI и `pre-push-check.sh`. В тестах шлюз всегда `FakeOrderGateway` (сети нет).

**Исполнитель Freedom (задача 66) — ВЫКЛЮЧЕН по умолчанию, та же архитектура, что у T-Invest.** Движок один (`PlanExecutionEngine`); брокер-специфика — адаптеры `IExecutionVenue`
(`TInvestExecutionVenue` — поведение задачи 65 без изменений, `FreedomExecutionVenue`) поверх ЕДИНСТВЕННЫХ шлюзов (`ITInvestOrderGateway`, `IFreedomOrderGateway`). **Ворота — по брокеру**
(`IExecutionGateResolver.ResolveForBrokerAsync`): для строк Freedom те же env `Execution__Enabled`, режим `live` и пауза, но ключ — `freedom_trade` (ok + `verification=network` в meta И его `accountNumber` =
`accountNumber` ПРОВЕРЕННОГО ключа на чтение `freedom`). Ключ на чтение остаётся у синка. Валидатор `freedom_trade` (`FreedomTradeCredentialValidator`): `GetAllUserTexInfo` с ТОРГОВЫМИ ключами → тот же
`trader_systems_id`, что у ключа на чтение (контекст `CredentialValidationContext.FreedomReadAccountNumber`); право торговать без заявки не проверить — в meta `tradeRight: "unverified"`, в UI «торговое право
проверится на первой заявке». На каждом тике торговый ключ сверяется у брокера (счёт = `accounts.broker_account_id` Freedom; иначе секрет `invalid` и остановка Freedom). `PolicyService.SaveAsync`: `dry_run = 0`
только если открыты ворота ВСЕХ брокеров строк; `AutoApproved` со строками Freedom — всегда dry-run. **Смешанный план:** у плана с закрытыми воротами всех брокеров — остановка как в задаче 65; иначе строки брокера
с закрытыми воротами закрываются (`halted`, висящие снимаются), строки брокера с открытыми — исполняются по общим правилам (продажи раньше покупок, по одной). **Только ручные:** план со строками Freedom стартует
лишь из `Approved` с `decided_via` ≠ `auto` (`FreedomAutoUsd = 0`); защита в глубину — `FreedomExecutionVenue.LineRejection`.
**Шлюз Freedom** (`FreedomOrderGateway`, Scoped): ключ `freedom_trade` читает сам; чтение (профиль, котировки, портфель, сделки, движения денег) — через `IFreedomApiClient` торговым ключом; команды заявок — через
общий подписанный транспорт `FreedomSignedHttp` (HttpClient `freedom`, ретраев нет). `PostLimitOrderAsync` ДО сети: ворота Freedom (`gate_closed`), сторона → `action_id` ТОЛЬКО из белого списка
`FreedomOrderRules.AllowedActionIds` {1 покупка, 3 продажа} — 2/4 (маржа) невозможны (`margin_forbidden`), тикер только `.US`/`.EU` (`.ITS` — `its_forbidden`, прочее — `ticker_forbidden`), qty/цена/`user_order_id`
корректны. Тело: `{instr_name, action_id, order_type_id: 2 (limit), qty, limit_price, expiration_id: 1 (day), user_order_id}`. Ответ: `error`/`errMsg` или HTTP 4xx — `Rejected` (заявки нет); сеть, таймаут,
HTTP 5xx/408/429, не-JSON, нет `order_id` — `Transient` (исход неизвестен → только сверка). Статусы `stat` (`FreedomOrderStatusMap`) КОНСЕРВАТИВНО: окончательные только 3 исполнена, 4 снята, 7 отклонена (и
`leaves_qty = 0`), 2 — частично, ЛЮБОЙ иной код — «активна» (сверка и снятие, без перевыставления). **Идемпотентность:** `user_order_id` (числовой, `NewUserOrderId`) пишется в `trade_orders.client_order_id`
ДО отправки (`trade_orders.broker = freedom`, миграция 037); ответ потерян — заявка ищется в `getNotifyOrderJson` (все, затем активные) по id брокера, иначе по `user_order_id` (`UserOrderIdMatches`: точно или
`префикс:число`), не нашлась за `UnconfirmedOrderTimeout` — `Failed`/`unknown_state`, повторно НЕ отправляется. **Рынок:** котировка `getStockQuotesJson` (bid/ask; метки времени у Freedom нет — момент
котировки = момент запроса, часы торгов проверяются отдельно; валюта котировки ≠ валюта строки — «нет котировки»); деньги — остаток валюты из `getPositionJson` МИНУС все активные заявки Freedom пользователя
в этой валюте (консервативно); продажа — остаток бумаги по тикеру. Часы: `.US` 17:30–23:00 МСК, `.EU` 11:00–18:30 МСК (`PolicyOptions.FreedomUs/FreedomEu`), снятие за `CancelBeforeClose` (10 мин); снятие —
`delTradeOrder` по id брокера. **Комиссия** (`FeesFromStatement`): при завершении строки и на каждом отложенном тике — сделки заявки (`getTradesHistory`, связь по `order_id`; средняя цена штуки = Σ p·q / Σ q)
→ строки `commission_for_trades` «(Trade N buy TICKER) Рынок: …, тип бумаги: …» (`FreedomCommissionParser`) → факт = Σ |sum|, модель = `FreedomFeeModel.Fee` по ФАКТИЧЕСКОМУ маршруту (europe / kazakhstan_its
(funds — отдельная ставка) / usa), `perOrder` один раз на заявку (`FreedomFeeReconciliation`), тариф — из `GetAllUserTexInfo` торгового ключа. Нет строк комиссии по всем сделкам — сверка откладывается до
`Execution:FreedomMaxFeeRechecks` (45 тиков ≈ 15 мин), план закрывается после сверки; расхождение > `FeeMismatchThreshold` — уведомление с маршрутом. Модель только для USD-заявок (иначе — оценка плана).
**Смена ключа:** удаление/замена ДРУГИМ `freedom_trade` при активных заявках Freedom — 409 `ActiveOrders` (заявки T-Invest не мешают и наоборот: `ITradeOrderRepository.ListActiveByUserAsync(user, broker)`),
`?cancelActive=true` — `TradeTokenChangeGuard.RunAsync(..., broker: freedom)` снимает их ТЕКУЩИМ ключом (заявку без id брокера сначала находит по `user_order_id`). «Снять нельзя» — то же одно уведомление
`halt_alerted_at`, что у T-Invest.

**Боевая проверка Freedom — делает ВЛАДЕЛЕЦ, один раз (песочницы у Freedom нет; агенты её НЕ запускают).** Цель — подтвердить на живом счёте то, что нельзя проверить без заявки: право торговать у
`freedom_trade`, форму `user_order_id` в ответах брокера, коды `stat` и формат `getNotifyOrderJson`/`delTradeOrder`. Минимальный риск: 1 штука, лимит ДАЛЕКО ниже рынка (не исполнится), сразу отмена.
1. Создать в веб-терминале Freedom ОТДЕЛЬНУЮ пару API-ключей с правом торговли; в «Подключениях» сохранить её как «Freedom (торговля)» — статус «Подключено» (счёт совпал с ключом на чтение).
2. На сервере временно `Execution__Enabled=true`, в настройках исполнения — режим LIVE (подтверждение `LIVE`); убедиться, что других подтверждённых живых планов нет (`/plans`), иначе сначала их отклонить.
3. Предусловия: свободно в USD ≥ цены 1 шт. `SPYM.US` + комиссия (лимитная покупка блокирует деньги); пауза снята. Исполнитель НЕ пропустит лимит далеко от рынка (дрейф 0.2% — так задумано),
   поэтому на время теста задать env `Execution__MaxPriceDriftFraction=0.6` (других живых планов нет — п. 2). Произвольной заявки в API нет — план вставить SQL-ом на сервере (подставить свои
   `@user`, `@freedomAccount` = `accounts.id` счёта Freedom, `@limit` ≈ 50% текущей цены, шаг 0.01):
   ```sql
   INSERT INTO trade_plans (user_id, rule, `trigger`, status, dry_run, created_at, expires_at, summary_json)
   VALUES (@user, 'MANUAL', 'freedom live test', 'PendingApproval', 0, UTC_TIMESTAMP(), UTC_TIMESTAMP() + INTERVAL 30 MINUTE, '{"portfolio":"usd","currency":"USD"}');
   INSERT INTO trade_plan_lines (plan_id, line_no, broker, account_id, instrument_key, ticker, side, quantity_lots, lot_size, limit_price, est_amount, est_fee, currency, reason)
   VALUES (LAST_INSERT_ID(), 1, 'freedom', @freedomAccount, 'SPYM.US', 'SPYM.US', 'Buy', 1, 1, @limit, @limit, 0.01, 'USD', 'боевая проверка, лимит далеко от рынка');
   ```
   В торговые часы `.US` (17:30–23:00 МСК, будни) подтвердить план ВРУЧНУЮ на странице «Планы» (подтверждение продлит срок до конца сессии).
4. Дождаться в Telegram «заявка отправлена»; в терминале Freedom увидеть заявку 1 шт. по лимиту, сверить: лимитная, на день, БЕЗ маржи. В `trade_orders` — `broker = freedom`, `client_order_id` (10 цифр),
   `broker_order_id`; записать, как брокер показал `user_order_id` в `getNotifyOrderJson` (точно или `api:…`) и код `stat` активной заявки.
5. Снять заявку кнопкой «Отменить» на странице «Планы» (у плана, вставленного SQL, нет nonce — кнопки в боте не будет) или `POST /api/plans/{id}/cancel-execution`; убедиться, что в терминале она снята, в `trade_orders` — `Cancelled`, записать код `stat` снятой заявки.
6. Вернуть `Execution__Enabled=false` (и `MaxPriceDriftFraction`, если меняли), режим — наблюдение. Если `stat` или форма `user_order_id` отличаются от ожидаемых (`FreedomOrderStatusMap`,
   `FreedomOrderRules.UserOrderIdMatches`) — НЕ включать исполнитель Freedom, завести задачу на правку маппинга. Реальные ответы брокера в репозиторий НЕ коммитить (публичный репо) — только синтетические фикстуры.

**Кандидаты-облигации (P2/P3b/P4).** Отбор как у `source=recommended` (гигиена, не флоатер, ликвидность не Caution), но светофор — только Green; ранг — доходность к худшему после налога
(`CandidateYieldRanking`), стакан и метаданные брокера запрашиваются только у верхушки пула (`BondCandidatePool` 25; недостающие метаданные — `TradingMetaRefresher.EnsureTInvestAsync`). Покупка — тот же
`CashAllocationService.Allocate` (ось сектора, 1 лот на бумагу), но с РЕАЛЬНЫМ лотом и ценой по стакану (существующие вызовы `AllocationEndpoints` не менялись). P3b: after-tax доходность облигации ≥ after-tax ожидаемая доходность фонда
ликвидности (`FundOutlookService`, взвешено по стоимости) + премия; лимит эмитента — `user_settings.default_max_concentration_percent` от стоимости облигаций (учитывает и бумаги того же эмитента, набранные ранее в этом же плане); облигации с нерублёвым номиналом в автоподбор не попадают (пропуск с причиной); нет подходящих — плана нет («замены нет»).

**Налог продаж (P5, P3b, P4, выход).** `SaleTaxEstimator` по СРЕДНЕЙ цене входа (журнал без FIFO-лотов — расхождение с фактом брокера возможно), плоские 13%, без сальдирования и ЛДВ; журнал неполон — оценка сверху 13% с выручки и
предупреждение. P5: сначала свободные деньги, затем тиры 0→1→2 (hold и tier ≥ 3 — только по `includeBaskets`), внутри тира — по возрастанию ставки налога (налог ÷ выручка, убыточные первыми); сумма — «к выводу после налога и комиссии»; Freedom — предупреждение о
минимальной комиссии вывода $20. Сторонние (не своей УК) фонды в авто-покупки идут, только если в журнале есть сделка и списанная комиссия (`PolicyOptions.OwnFunds` — свои).

Фронт: страница «Планы» (`/plans`, `pages/Plans.tsx`, `usePlansStore`, `components/plans/*`, баннер «Режим наблюдения: сделки не отправляются»), блок «Планы сделок» на «Капитале» (`components/capital/PlanActions.tsx`:
«Распределить сумму…», «Вывести…», «Выйти из временной корзины»).

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
