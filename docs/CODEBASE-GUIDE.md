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
| MOEX ISS: YIELD | вход | **проценты** → храним долю (÷100) |
| MOEX ISS: DURATION | вход | **дни** → храним годы (÷365) |
| MOEX ISS: цены (PREVPRICE/LAST/CLOSE) | вход | **% от номинала** → рубли = `pct/100 × FaceValue` |
| MOEX ISS `marketdata_yields`: EFFECTIVEYIELD, YIELDTOOFFER | вход | **проценты** → доля (÷100) |
| MOEX ISS `marketdata_yields`: GSPREADBP, ZSPREADBP | вход | **базисные пункты** → доля (÷10000: 35 б.п. = 0.0035) |
| MOEX ISS карточка `description`: COUPON_BENCHMARK_SPREAD | вход | **проценты** (строка "1.90") → доля (÷100: 0.019), `security_details.coupon_benchmark_spread_fraction` |
| MOEX ISS `securities`: ACCRUEDINT (НКД) | вход | **рубли на одну бумагу** (не %, не на лот) — конверсии нет |
| T-Invest `GetPortfolio` (CurrentPrice/CurrentNkd/AveragePositionPrice) | вход | **рубли** за бумагу — конверсия не нужна |
| T-Invest `GetLastPrices` (marketdata `LastPrice`) | вход | **ПУНКТЫ** (% от номинала) → рубли = `pts/100 × FaceValue` (см. `LiveQuoteConverter`) |
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
Правило сравнимости доходностей: floater/indexed/dataIncomplete — «вне сравнения»
(`YieldComparability.IsComparable`). Порог отклонения спреда (RV-вердикт и риск-сигнал кандидата) —
одна константа `SpreadThresholds.DeviationFraction` (0.0020 = 20 б.п.). Ставка комиссии — ТОЛЬКО через
`ICommissionRateProvider` (override → оценка из журнала → дефолт), не константа.

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
эмитента — `GetEmitentDefaultHistoryAsync` (потребители — светофор, задача 46); точный движок — по требованию через
`POST /api/universe/{secid}/materialize` (единый путь обогащения —
`InstrumentEnrichmentService`).

## Проверка перед завершением любой задачи

```bash
scripts/pre-push-check.sh --all   # зеркало обоих CI-пайплайнов + гварды, сводка в конце
```
Интеграционным тестам нужен Docker. Не заявлять «готово» без фактического вывода.
