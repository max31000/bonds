# Задача 23 — Честная матрица замен: серверный перебор + объяснимость + % годовых

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Конвенции — [CLAUDE.md](../CLAUDE.md).
> Доходности на бэкенде — ДОЛИ. Формулировки в UI — оценочные («кандидат/оценка»), дисклеймеры
> обязательны ([Disclaimer.tsx](../bonds-web/src/components/Disclaimer.tsx)).
>
> **Зависимости:** задача 22 (`ICommissionRateProvider`) — ставка комиссии берётся из него.

---

## Проблема

Секция «Замены» не отвечает строго на вопрос «какая замена САМАЯ выгодная»: перебор пар живёт на
фронте ([useRecommendationsStore.ts](../bonds-web/src/store/useRecommendationsStore.ts) —
`buildReplacementRequests`: топ-3 слабых × топ-2 целей, максимум 6 запросов `POST /replacement`),
watchlist в перебор почти не попадает, отвергнутые пары невидимы. Карточка показывает голое
«выгода ≈ 100 ₽ за 4 мес» без формулы — непонятно, откуда число и много это или мало
(100 ₽ на позиции 1 500 ₽ за 2 мес — это ~40% годовых, но этого не видно).

## Что сделать

### A. Серверный перебор — `GET /api/analytics/replacement-matrix`

Новый эндпоинт в [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)
(логика — в новом чистом сервисе `Bonds.Core/Analytics/ReplacementMatrixService.cs` + оркестрация
в эндпоинте по образцу соседей):

1. Кандидаты-hold: все сравнимые позиции портфеля (НЕ floater/indexed/dataIncomplete — то же
   правило, что в [PositionComparisonService.cs](../src/Bonds.Core/Analytics/PositionComparisonService.cs)).
   Кандидаты-target: сравнимые позиции + **watchlist-бумаги** (через
   `PortfolioHoldingsBuilder.BuildForInstrumentsAsync`, как это уже делает `PostReplacement`
   с `TargetInstrumentId`).
2. Для КАЖДОЙ пары hold×target с `targetYield > holdYield`: горизонт =
   `max(min(hold.DaysToHorizon, target.DaysToHorizon), 1) / 365` (та же формула, что сейчас на
   фронте — `horizonYearsFor`); окно сравнимости дюраций ±1.5 года — но пары вне окна НЕ
   выбрасывать, а помечать `rejectedReason: "durationMismatch"` (см. п.4). Расчёт —
   существующий `SwitchAnalysisService.Compare` со ставками из `ICommissionRateProvider` (задача 22).
3. Для каждой пары в ответ — полная разбивка: `spreadFraction`, `capitalRub`
   (netProceedsAfterSale), `horizonYears`, `grossGainRub`, `sellCommissionRub`, `buyCommissionRub`,
   `netBenefitRub`, **`annualizedBenefitFraction`** = netBenefit / capital / horizonYears (доля,
   задокументируй формулу), `commissionRateUsed`, `commissionRateSource`, `isWatchlistTarget`.
4. Ответ: `bestPairs` (все с netBenefit > 0, отсортированы по netBenefit убыв.) +
   `rejectedPairs` (netBenefit ≤ 0 → reason "notProfitable" с числом; вне окна дюраций →
   "durationMismatch"; targetYield ≤ holdYield не включать вообще — их слишком много и они
   тривиальны) + `disclaimer`. Портфель ~15 бумаг → максимум ~200 пар, считается мгновенно,
   лимитов не нужно; но добавь разумный предохранитель (если пар > 2000 — вернуть топ-500 по
   каждой категории, задокументируй).
5. `POST /api/analytics/replacement` НЕ удалять (обратная совместимость), но фронт на него
   больше не ходит.

Интеграционные тесты: засеянный портфель из 4 бумаг + 1 watchlist → матрица содержит
watchlist-пару; ранжирование по netBenefit; отвергнутые с причинами; ставка комиссии из
настроек применена.

### B. Frontend — переработка секции «Замены»

[Recommendations.tsx](../bonds-web/src/pages/Recommendations.tsx) + store + [recommendations.ts](../bonds-web/src/api/recommendations.ts):

1. Store: убрать `buildReplacementRequests`/цикл `postReplacement` — один запрос матрицы.
2. Карточки лучших пар (как сейчас, но с данными матрицы): заголовок «A → B», крупно
   «выгода ≈ X ₽ (~Y% годовых) за Z», значок watchlist-цели. **Раскрывашка с формулой** —
   построчно: «спред доходностей: 2.1 п.п.» / «капитал после продажи: 1 480 ₽» /
   «горизонт: 7 мес (до оферты A)» / «валовая выгода: 182 ₽» / «− комиссия продажи 0.68 ₽ −
   комиссия покупки 0.68 ₽ (0.046%, из ваших сделок)» / «= чистая выгода 181 ₽ ≈ 21% годовых».
3. Свёрнутый блок «Все рассмотренные пары (N)» — компактная таблица: пара, netBenefit
   (красный/зелёный), причина отклонения по-русски («невыгодна: −12 ₽», «дюрации несопоставимы»).
4. Пустое состояние: «выгодных замен не найдено — рассмотрено N пар» (число из ответа) вместо
   нынешней безликой фразы.
5. Vitest: рендер разбивки, watchlist-значок, таблица отвергнутых, пустое состояние с числом.

## Критерии приёмки

- [ ] Матрица считает ВСЕ пары (портфель+watchlist) на сервере со ставкой из задачи 22.
- [ ] Каждая пара несёт полную разбивку и % годовых; отвергнутые видимы с причинами.
- [ ] Фронт делает один запрос; карточка раскрывается в формулу; «6 запросов с фронта» удалены.
- [ ] `POST /replacement` не сломан (контракт сохранён, интеграционные тесты старого эндпоинта зелёные).
- [ ] `dotnet build/test`, `yarn typecheck/test:run/build/lint` — зелёные.
