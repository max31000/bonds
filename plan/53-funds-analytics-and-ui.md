# Задача 53 — Фонды в продукте: P&L, фактическая доходность, прогноз, экраны, общий баланс

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (паттерны и контракт единиц — читать обязательно).
>
> **Зависимости:** 51 (данные фондов: `funds`, `fund_positions`, `fund_operations`,
> `fund_price_history`, `fund_distributions`, `market_index_history`, репозитории), 52
> (`PositionIncomeCalculator`/`IncomeLedgerBuilder` — P&L фонда считать ИМ, не дублировать).
> **Порядок в волне:** строго после 52; до 54.
>
> **Рамки (что НЕ делать):** не подмешивать фонды в облигационные аналитики (композиция по
> дюрации, сценарии ставок, траектория, рекомендации, скринер, вердикты, сигналы, cash-flow
> облигаций). Не трогать облигационный XIRR/снимки — XIRR «всего портфеля» берётся из задачи 52.
> Формулировки — «оценка», без «купите/продайте»; дисклеймеры обязательны.

---

## Проблема

Пользователь держит паи облигационных БПИФ Т-Капитала: TPAY «Пассивный доход» (ежемесячные
выплаты, флоатеры и короткие облигации) и TOFZ «Т-Капитал ОФЗ» (накопительный, ОФЗ, следует
T-Bank OFZ Bond Index). Хочет видеть их в общем балансе, P&L и «прочие параметры», а также
оценку ожидаемой доходности. Публичного API с доходностью портфеля фонда у УК нет; что есть:
история цены пая и выплат на пай (задача 51), комиссия фонда TER (доля), доходность к
погашению индекса ОФЗ RGBI (MOEX).

## Что сделать

### A. Аналитика фонда (Core, чистые функции)

1. `Bonds.Core/Funds/FundPerformanceCalculator.cs` — фактическая доходность по рыночным данным
   (не зависит от владения): окна 1м/3м/6м/12м и «с начала владения» (от первой покупки
   пользователя): `TR = (P_end + Σ выплат на пай с record_date в (start, end]) / P_start − 1` (ДОЛЯ),
   `P_start` — последняя цена ≤ даты начала окна (forward fill), окно без цены на старте → null.
   Для 12м — это и годовая; для окон < 12м отдавать сырую и аннуализированную
   (`(1+TR)^(365/days) − 1`) — UI покажет сырую, годовую в тултипе.
2. `Bonds.Core/Funds/FundOutlookService.cs` — **оценка** ожидаемой годовой доходности. Метод
   (`FundForecastMethod`; `Auto` резолвится так):
   - `Distributions` — если за последние 6 мес. ≥ 2 выплат: `y = Σ выплат на пай за последние 92 дня × (365/92) / текущая цена`
     (выплаты уже после TER). Плюс `expectedMonthlyIncomeRub = последняя выплата на пай × qty`.
   - `IndexYield` — иначе, если задан `benchmark_index` или фонд ОФЗ-направленности (`focus_type`
     fixed_income И имя/тикер содержит «ОФЗ»/«OFZ») → индекс `RGBI`: `y = yield_fraction(последняя точка, не старше 10 дней) − TER (TER null → 0 + флаг TerUnknown)`.
   - `TrailingReturn` — иначе: 12м TR, если истории ≥ 180 дней (иначе — окно, что есть, ≥ 90 дней,
     аннуализированное; меньше — null «мало данных»).
   Явный `forecast_method` ≠ Auto — используется он (если данных нет — null + причина, без
   тихого фолбэка).
   Выход: `ExpectedAnnualYieldFraction?`, `AfterTaxYieldFraction?` (через `AfterTaxYield` с единой
   ставкой НДФЛ — как в задаче 50; упрощение «ко всей доходности» подписать), `Method`, `BasisLabel`
   (человекочитаемо: «по последним выплатам за 3 мес.», «доходность индекса RGBI 16.03% − комиссия
   фонда 0.79%», «фактическая доходность за 12 мес.»), `ExpectedIncome1yRub` (= MV × y),
   `ExpectedMonthlyIncomeRub?`, `Confidence (Low|Medium|High)`, `AsOf`, `Warnings[]`
   (`TerUnknown`, `IndexStale`, `FewDistributions`, `ShortHistory`).
   Для фондов с переменным купоном подписать: выплаты следуют за ключевой ставкой — при её
   снижении будут ниже (константа текста на фронте).

### B. API

1. `FundsEndpoints` (`Program.cs`):
   - `GET /api/funds` → `{ asOf, funds: FundRowDto[], totals: { marketValueRub, investedRub,
     unrealizedPnlRub, incomeNetRub, totalResultRub }, disclaimer }`. `FundRowDto`: `fundId, ticker,
     name, managementCompany, quantity, currentPriceRub, priceAsOf, marketValueRub, avgCostRub,
     investedRub, unrealizedPnlRub, unrealizedPnlFraction, distributionsGrossRub, taxesRub,
     totalResultRub, totalReturnFraction, xirrFraction, totalExpenseFraction, performance:
     { m1, m3, m6, m12, sinceFirstBuy } (ДОЛИ, nullable), outlook: FundOutlookDto, shareOfPortfolioFraction`.
     P&L-поля — из `PositionIncomeCalculator` (через `IncomeLedgerBuilder` задачи 52 или его публичный
     метод для одного/набора фондов) — одна методика с «Доходами».
     `shareOfPortfolioFraction` = MV фонда / (облигации + фонды), без денег.
   - `GET /api/funds/{fundId}` → `FundDetailDto`: строка + `priceHistory` (≤ 2 лет, `{date, closeRub}`),
     `distributions` (`{recordDate, paymentDate, amountPerShareRub, yieldOnPriceFraction}`),
     `operations` (журнал пользователя по фонду), `outlook` полностью, `settings: { forecastMethod,
     benchmarkIndex }`. Фонд, который пользователь уже продал, тоже отдаётся (qty 0, история есть).
   - `PUT /api/funds/{fundId}/settings` `{ forecastMethod, benchmarkIndex? }` — валидация enum/кода
     индекса (`^[A-Z0-9]{2,16}$`) → 422.
2. Общий баланс: `GET /api/portfolio/summary` (новый, в `FundsEndpoints` или отдельном
   `PortfolioSummaryEndpoints`) → `{ bondsValueRub, fundsValueRub, cashRub?, totalRub,
   brokerTotalRub?, brokerDiffRub?, balanceSyncedAt, portfolioXirrFraction? }` — облигации через
   `PortfolioHoldingsBuilder` (единое правило стоимости), фонды из `fund_positions`, деньги/брокерский
   итог — `accounts`. XIRR портфеля — из сервиса задачи 52 (не пересчитывать иначе).
3. DTO — `Endpoints/Dtos/FundsDtos.cs`; доли — ДОЛИ.

### C. Frontend

1. `api/funds.ts` + типы в `api/types.ts` (буквально по DTO), стор `store/useFundsStore.ts`
   (список, деталь, сохранение настроек; независимая загрузка).
2. **Позиции** (`pages/Positions.tsx`): сверху `SegmentedControl` «Облигации | Фонды» (состояние в
   URL `?tab=funds`, по умолчанию облигации — существующая таблица без изменений). Вкладка «Фонды»:
   `Section` с таблицей (Фонд/тикер, Кол-во, Цена, Стоимость, Ср. цена, P&L ₽/%, Выплаты получено
   (нетто), Доходность 12м (факт), Оценка доходности (с бейджем метода и тултипом `basisLabel`),
   Доход в мес. (оценка), Доля портфеля) + строка итогов; мобильная версия — карточки по паттерну
   `PositionCard`. Пусто → `EmptyState` «Фондов нет — они появятся после синка, если куплены в Т-Инвестициях».
3. **Карточка фонда** `pages/FundDetail.tsx`, роут `/funds/:fundId` (`App.tsx`): `MetricGrid`
   (стоимость, P&L, выплаты нетто, XIRR, TER, оценка доходности); график цены пая (`ChartFrame` +
   объяснение в `components/charts/explanations.ts`) с линией средней цены входа и маркерами
   выплат; график выплат на пай по месяцам (бары) с доходностью выплаты к цене; блок «Прогноз
   (оценка)»: метод, база, ожидаемый доход за год/в месяц, после-налоговая, предупреждения, селект
   метода (Авто/По выплатам/По индексу/По факту) → `PUT settings` → рефетч; «Мои операции».
   Дисклеймер (`components/Disclaimer.tsx`) + текст про зависимость выплат от ключевой ставки.
4. **Обзор** (`pages/Dashboard.tsx`): KPI «Стоимость портфеля» = облигации + фонды + деньги из
   `/api/portfolio/summary` (подсказка с разбивкой; если `|brokerDiffRub|` > 1% — подсказка
   «расходится с брокером на X ₽ (другие активы/валюта)»); KPI «Фонды» (стоимость + P&L) — только
   если фонды есть; XIRR: существующий облигационный переименовать в подписи «XIRR облигаций»,
   рядом/в подсказке — «XIRR портфеля» из summary. Живой интрадей облигаций не трогать. Отказ
   `/api/portfolio/summary` не роняет остальные виджеты.
5. Vitest + MSW: вкладка фондов рендерит строки и итоги; `?tab=funds` открывает вкладку;
   FundDetail — метрики, смена метода прогноза отправляет PUT; Dashboard — общий баланс с
   фондами и деньгами, подсказка расхождения; пустые/ошибочные состояния. Дефолтные хендлеры —
   `test/msw-handlers.ts`. Даты в тестах — относительные.

### D. Тесты backend

- Юнит `FundPerformanceCalculator`: эталон — цены 100 → 101, 3 выплаты по 1.2 в окне → TR =
  (101 + 3.6)/100 − 1 = 0.046; forward fill цены; нет цены на старте → null.
- Юнит `FundOutlookService`: выплаты 1.3/1.25/1.2 за 92 дня при цене 100 → y = 3.75 × 365/92 / 100
  ≈ 0.14878; IndexYield: RGBI 0.1603 − TER 0.0079 = 0.1524; устаревший индекс → `IndexStale`; Auto
  для TOFZ-подобного фонда без выплат → IndexYield; явный метод без данных → null + причина.
- Интеграционные: `/api/funds` и `/api/funds/{id}` на засеянных данных (включая проданный фонд);
  PUT settings + 422; `/api/portfolio/summary` складывает облигации + фонды + деньги; 401-теория.

## Критерии приёмки

- [ ] Фонды видны на «Позициях» (вкладка) и в карточке фонда с P&L, выплатами, фактической
      доходностью по окнам и подписанной оценкой ожидаемой доходности (метод + база + дисклеймер).
- [ ] Общий баланс на «Обзоре» = облигации + фонды + деньги, со сверкой с брокерским итогом.
- [ ] P&L фонда совпадает с его строкой на странице «Доходы» (одна методика, задача 52).
- [ ] Облигационные экраны/аналитики не изменили поведения (тесты зелёные без ослабления).
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
