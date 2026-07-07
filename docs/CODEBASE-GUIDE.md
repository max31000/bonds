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
statusCode: 422)`. Юзер — `ResolveUserId(ClaimsPrincipal)`-хелперы по образцу соседей.

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

**Ключевые расчётные сервисы** (чистые, `Bonds.Core`): движок — `Calculation/*` (YTM, дюрации,
НКД, XIRR, G-спред: интерполяция кривой — `GSpreadCalculator.CurveValue`); портфельные —
`Analytics/*` (cost basis, матрица замен, аллокация/корзина, what-if, налог продажи
`SaleTaxEstimator`, RV `RelativeValueService`, бакеты — `DurationBucketClassifier`). Правило
сравнимости доходностей: floater/indexed/dataIncomplete — «вне сравнения»
(`ReplacementMatrixService.IsComparable`). Ставка комиссии — ТОЛЬКО через
`ICommissionRateProvider` (override → оценка из журнала → дефолт), не константа.

## Тесты

**Юнит** (`tests/Bonds.Tests`, папки по доменам): без сети и без БД. HTTP-клиенты — вложенный
`FakeHandler : HttpMessageHandler` + Moq на `IHttpClientFactory` (образцы —
`Moex*PagingTests`). Реальные JSON-снимки MOEX — `Fixtures/Moex/*.json`.

**Интеграционные** (`tests/Bonds.IntegrationTests`, плоско по эндпоинтам):
`TestWebApplicationFactory` (env=Testing: миграции на старте пропускаются, hosted services
вырезаны, JWT/DataProtection тестовые) + `DatabaseFixture` (Testcontainers MySQL 8, миграции
прогоняются фикстурой) + `[Collection("Integration")]` (общий контейнер). Авторизованный клиент —
`JwtTestHelper.GenerateToken(userId)`. Внешние клиенты — `factory.WithWebHostBuilder(...
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
сериализацией, был кейс `Unknown` vs `None`). Графики — chart-kit `components/charts/`
(ChartCard/ChartTooltip/константы/ChartExplainIcon — у каждого графика «?»-объяснение).
Форматтеры — `utils/format.ts`. Мобильная адаптация — карточки по паттерну `PositionCard` +
`useMediaQuery`. Роуты — `App.tsx`, навигация — `AppLayout.tsx` (там же индикатор синка, burger,
переключатель темы — не ломать).

## Продуктовые константы

Single-user, один счёт. Т-Invest токен — read-only секрет: не логировать, не отдавать на фронт,
хранится шифрованным (DataProtection, ключи на volume). Все аналитики — «оценки, не
инвестрекомендации»: дисклеймеры обязательны (`components/Disclaimer.tsx`), формулировки
«кандидат/оценка», не «купите». Двухъярусная архитектура данных: банк (`bond_universe`) —
дешёвая статистика MOEX для ранжирования; точный движок — по требованию через
`POST /api/universe/{secid}/materialize` (единый путь обогащения —
`InstrumentEnrichmentService`).

## Проверка перед завершением любой задачи

```bash
scripts/pre-push-check.sh --all   # зеркало обоих CI-пайплайнов + гварды, сводка в конце
```
Интеграционным тестам нужен Docker. Не заявлять «готово» без фактического вывода.
