# Этап 12 — Исправление ошибок корректности расчётов

> **Кому адресовано.** Агент-исполнитель (**Sonnet**). Это список независимых задач-багфиксов. Идти
> по порядку приоритета (T-1 → T-10), но задачи независимы — можно делать по одной за раз и пушить.
>
> **Контекст и доказательства.** Полный аудит с боевыми данными — [CORRECTNESS_AUDIT.md](../CORRECTNESS_AUDIT.md).
> Каждая задача ниже ссылается на ID находки оттуда (N-1, C-1, H-1 …). Числа в тестах взяты с реального
> портфеля (счёт `2040378115`) — не выдумывай свои, используй приведённые.
>
> **Рабочий процесс для КАЖДОЙ задачи (TDD):**
> 1. Сначала напиши тест из раздела «Воспроизведение» — он должен **упасть** (краснота = баг воспроизведён).
> 2. Внеси фикс из раздела «Починка».
> 3. Прогони тот же тест — должен **пройти**. Прогони весь сосед­ний набор тестов на регресс.
> 4. Перед пушем — **обязательно** `pre-push-check` (см. [CLAUDE.md](../CLAUDE.md)); пушить только на зелёном.
>
> **Бэкенд-тесты:** xUnit в `tests/Bonds.Tests/...` (детерминированные, без сети — MOEX/HTTP мокать
> фейковым `HttpMessageHandler`). **Фронт-тесты:** vitest в `bonds-web/src`. Не ходить в сеть из тестов.
>
> **Конвенция единиц (не нарушать):** все доходности на бэкенде — доли (0.12 = 12%); проценты на фронте
> делает форматтер. Рубли — `decimal`. Дюрация — годы. G-спред — доля (0.015 = 150 б.п.).

---

## T-1 🔴 Полный график купонов из MOEX (пагинация ISS) — находка N-1

**Самая важная задача. Чинит большинство неверных YTM/дюраций/G-спредов/денежного календаря.**

### Что за баг
`MoexIssClient.GetBondizationAsync` запрашивает `bondization` **без пагинации**, а ISS отдаёт блок
`coupons` страницами по **20 строк**. У любой бумаги с >20 купонов (все помесячные/длинные выпуски)
хвост графика молча теряется. Следствие: YTM/дюрация/выпуклость/G-спред и денежный календарь считаются
по обрезанному потоку; флаг `DataIncomplete` при этом **не** ставится (нарушение §4.4). Боевой пример:
РЖД 1Р-37R (RU000A10AZ45) — в MOEX 42 купона, сервису доехало 20 (последний 2026-10-19), после даты
расчёта осталось ~4 → YTM = **−1.47%** вместо истинных ≈13–14%.

- **Файлы:** [src/Bonds.Infrastructure/Connectors/Moex/MoexIssClient.cs](../src/Bonds.Infrastructure/Connectors/Moex/MoexIssClient.cs) (`GetBondizationAsync`, стр. 61–82); парсер [MoexBondizationParser.cs](../src/Bonds.Infrastructure/Connectors/Moex/MoexBondizationParser.cs) и [IssTable.cs](../src/Bonds.Infrastructure/Connectors/Moex/IssTable.cs) — менять, скорее всего, не нужно (они парсят то, что дано).

### Воспроизведение (тест)
Новый файл `tests/Bonds.Tests/Connectors/MoexBondizationPagingTests.cs`.
- Поднять `MoexIssClient` с фейковым `HttpMessageHandler` (см. существующие тесты коннекторов в
  `tests/Bonds.Tests/Connectors` как образец инъекции `IHttpClientFactory`/`HttpClient`).
- Хендлер отдаёт по URL-параметру `start`:
  - `start` отсутствует или `=0` → JSON с блоком `coupons`: 20 строк купонов + блок `coupons.cursor`
    с `["INDEX","TOTAL","PAGESIZE"]` = `[0, 42, 20]` (плюс пустые `amortizations`, `offers`).
  - `start=20` → 20 строк, cursor `[20, 42, 20]`.
  - `start=40` → 2 строки, cursor `[40, 42, 20]`.
- **Тест `GetBondizationAsync_FetchesAllCouponPages`:** вызвать `GetBondizationAsync(secid)`, проверить
  `result.Coupons.Count == 42`. На текущем коде вернётся 20 → тест падает (баг воспроизведён).
- Дай хендлеру минимальный валидный JSON купона (поля, которые читает `MoexBondizationParser`:
  `coupondate`, `value_rub`/`value`, `isfixed`/`facevalue` — посмотри в парсере точные имена колонок,
  возьми их as-is). Достаточно одинаковых строк с разными датами.

### Починка (качественно)
В `GetBondizationAsync` реализовать дочитывание страниц. Рекомендуемый подход — пагинировать **каждый
блок** (`coupons`, `amortizations`, `offers`) отдельным запросом `iss.only={block}&start={n}`, цикл до
короткой/пустой страницы:
1. Завести приватный helper `FetchAllBlockRowsAsync(secid, blockName, ct)`:
   - цикл: `start = 0, 20, 40, …`; GET `…/bondization/{secid}.json?iss.only={blockName}&start={start}&iss.meta=off`;
     распарсить строки блока через `IssTable.Parse(root, blockName)`;
   - аккумулировать «сырые» строки; остановиться, когда страница вернула **0** строк (надёжнее, чем
     полагаться на cursor); инкремент `start` на размер вернувшейся страницы (или фиксированно 20 — ISS
     отдаёт 20).
   - предохранитель от бесконечного цикла: лимит, скажем, 50 страниц (1000 купонов) — этого хватит
     любой реальной бумаге; при достижении лимита — лог + `DataIncomplete=true`.
2. Собрать общий JSON/строки трёх блоков и отдать в `MoexBondizationParser` (либо адаптировать парсер,
   чтобы принимал уже собранные строки по блокам — выбери минимально инвазивный путь; парсер не должен
   терять имеющуюся логику меток `IsKnown`/неполноты).
3. Если любой из запросов страниц упал на середине (не первая страница) — пометить
   `MoexBondizationResult.DataIncomplete = true` (честная деградация, §4.4), не молча.
4. amortizations/offers тоже пагинировать тем же helper'ом (обычно <20, но единообразие дешевле бага).

> Альтернатива (если переписывать парсер дорого): оставить один запрос, но добавить `&limit=...` нельзя
> надёжно (ISS капит limit). Поэтому пагинация через `start` — правильный путь, не `limit`.

### Проверка фикса
- `MoexBondizationPagingTests` зелёный (42 купона).
- Доп. регресс-тест `tests/Bonds.Tests/Connectors/...`: при cursor TOTAL=8 (одна страница) вернуть 8 —
  убедиться, что короткие бумаги не ломаются и лишних запросов нет.
- **Чеклист:**
  - [ ] `result.Coupons.Count` для многокупонной бумаги = TOTAL, а не 20.
  - [ ] `DataIncomplete` ставится при сбое дочитывания, не молча.
  - [ ] amortizations/offers не потеряны.
  - [ ] `pre-push-check` зелёный.

---

## T-2 🔴 Валютные (замещающие) облигации с USD-номиналом — находка N-2

### Что за баг
Бумаги с номиналом в иностранной валюте (USD-номинал, «замещающие») считаются как рублёвые: цена в ₽
(~7629), номинал «100» (USD), купон из bondization в ₽-эквиваленте — смешанные единицы → YTM/дюрация/
G-спред бессмысленны (маркер: G-спред −800…−1600 б.п.), и бумага попадает в рублёвые агрегаты/scatter/
композицию. Боевые: НОВАТЭК1Р2 (RU000A108G70), СибурХ1Р03 (RU000A10AXW4), у обеих по MOEX `FACEUNIT=USD`.

Флаг `Instrument.IsOutOfScopeCurrency` в коде есть (§11), но: (а) `Instrument.Currency` не обновляется из
MOEX (остаётся «RUB» по умолчанию); (б) самое главное — **флаг почти никто не соблюдает**: его уважает
только `CashFlowProjectionService`, а `BondMetricsCalculator` / `PortfolioHoldingsBuilder` / эндпоинты
positions/scatter/composition/comparison считают и показывают метрики независимо от флага.

- **Файлы:** [BondSyncService.cs](../src/Bonds.Infrastructure/Sync/BondSyncService.cs) (`EnrichFromMoexAsync`, стр. 320–327); [BondMetricsCalculator.cs](../src/Bonds.Core/Calculation/BondMetricsCalculator.cs); [PortfolioHoldingsBuilder.cs](../src/Bonds.Infrastructure/Analytics/PortfolioHoldingsBuilder.cs); эндпоинты [PositionsEndpoints.cs](../src/Bonds.Api/Endpoints/PositionsEndpoints.cs), [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs); модель [Instrument.cs](../src/Bonds.Core/Models/Instrument.cs).

### Воспроизведение (тест)
1. `tests/Bonds.Tests/Sync/...` или `Calculation`: тест `OutOfScopeCurrency_SetFromFaceUnit` — прогнать
   маппинг `info.FaceUnit="USD"` → ожидать `IsOutOfScopeCurrency==true` и `Currency=="USD"` (сейчас
   Currency не выставляется → падает на проверке Currency).
2. `tests/Bonds.Tests/Analytics/PortfolioHoldingsBuilderTests.cs` (или ближайший): построить holding по
   инструменту с `IsOutOfScopeCurrency=true` и проверить, что в выдаче метрики не показываются как
   достоверные — `YtmEffective==null`, `GSpread==null`, `IsEstimated==true` (или holding исключён из
   рублёвых агрегатов). Сейчас метрики считаются → падает.

### Починка (качественно)
1. **Детекция валюты надёжно.** В `EnrichFromMoexAsync` помимо `IsOutOfScopeCurrency` выставлять
   `instrument.Currency = NormalizeCurrency(info.FaceUnit)` (SUR→RUB; иначе как есть: USD/EUR/…). Если в
   будущем появится `nominal.currency` из T-Invest — использовать его как доп. источник, MOEX
   приоритетнее. Маппинг вынести в чистую функцию (тестируемо).
2. **Соблюдать флаг во всех потребителях.** Самый чистый вариант — в `PortfolioHoldingsBuilder`/
   `BondMetricsCalculator`: если `IsOutOfScopeCurrency`, не считать YTM/дюрацию/выпуклость/PVBP/G-спред
   (вернуть `null`, как для флоатера), пометить `IsEstimated=true` и добавить `Note` «бумага в валюте
   {cur} — вне рублёвого контура MVP». Рыночную стоимость (`MarketValueRub`) можно оставить (она в ₽ от
   брокера) — но пометить.
3. **Агрегаты.** `PortfolioCompositionService` и сценарии/scatter должны исключать (или отдельно
   помечать) валютные бумаги из рублёвых разрезов, чтобы не искажать доли. Минимально — не рисовать им
   G-спред/scatter-точку.
4. **UI.** В таблице позиций показать бейдж «валютная / вне скоупа» (по аналогии с «данные неполные»).
   Фронт: `bonds-web/src/pages/Positions.tsx` + тип в `api/types.ts` (поле `isOutOfScopeCurrency`).

### Проверка фикса
- Тесты из «Воспроизведение» зелёные.
- **Чеклист:**
  - [ ] У USD-бумаги `Currency!="RUB"`, `IsOutOfScopeCurrency==true`.
  - [ ] YTM/дюрация/G-спред у неё `null`, не считаются; нет G-спреда −800 б.п.
  - [ ] Не искажает композицию/scatter; в UI помечена.
  - [ ] `pre-push-check` зелёный.

---

## T-3 🔴 Переписать модель «Траектории портфеля» — находки C-1, C-2, H-2, M-2, M-4

> Это **один** баг (неверная модель учёта в `PortfolioTrajectoryService.Compute`) с несколькими
> проявлениями. Чинится одним переписыванием метода — поэтому объединено в одну задачу. Off-by-one (M-2)
> и линейный реинвест (M-4) исправляются здесь же, т.к. живут в том же цикле.

### Что за баг
- **C-1:** `currentValue` (стоимость бумаг) держится константой на весь горизонт, а к кэшу каждый месяц
  прибавляется `NetRub`, который включает **возврат тела** (амортизация+погашение). При погашении тело
  учитывается дважды: и как стоимость бумаги (в `currentValue`), и как пришедший кэш. Боевой эффект:
  142 308 ₽ → 178 436 ₽ за месяц погашения ОФЗ 26226 (mv 29 099); к 2027-09 «стоимость» 216 299 (+52%).
- **C-2:** `cumulativeIncome += netFlow` считает возврат тела как доход (это возврат капитала).
- **H-2:** обе линии (с/без реинвеста) завышены одним и тем же корнем.
- **M-2:** цикл `for i=1..horizon` стартует со **следующего** месяца — поток текущего месяца теряется.
- **M-4:** реинвест `*(1 + reinvestRate/12)` — линейное деление годовой эффективной ставки.

- **Файлы:** [src/Bonds.Core/Analytics/PortfolioTrajectoryService.cs](../src/Bonds.Core/Analytics/PortfolioTrajectoryService.cs); эндпоинт [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs) (`GetTrajectory`).

### Корректная модель (реализовать ровно так)
Вход `MonthlyCashFlowSummary` уже разделяет купоны и тело: `CouponGrossRub`, `PrincipalGrossRub`, `TaxRub`
(налог только на купон). Для каждого месяца:
```
netCoupon    = m.CouponGrossRub - m.TaxRub      // чистый купонный доход
netPrincipal = m.PrincipalGrossRub              // возврат тела (налогом не облагается)
```
Вести два состояния: `bondValue` (старт = Σ MarketValueRub) и `cash` (старт = 0):
```
bondValue -= netPrincipal                       // тело УХОДИТ из стоимости бумаг
cash      += netCoupon + netPrincipal           // …и приходит в кэш (перенос, не новый капитал)
cash      *= monthlyFactor                       // реинвест: см. ниже (для «без реинвеста» factor = 1)
cumulativeIncome += netCoupon                    // ДОХОД = только купоны
portfolioValue = max(bondValue, 0) + cash
```
- **Реинвест (M-4):** `monthlyFactor = (1 + reinvestRate)^(1/12)` (для линии «без реинвеста» — `1`).
- **Off-by-one (M-2):** итерировать месяцы, **включая текущий** (с `i = 0`, месяц = первое число текущего
  месяца + i), чтобы не терять ближайшие поступления.
- Результат: при погашении `bondValue` падает на тело, `cash` растёт на ту же сумму → суммарная стоимость
  **не скачет**; «доход» растёт только на купоны.

### Воспроизведение (тест)
`tests/Bonds.Tests/Analytics/PortfolioTrajectoryServiceTests.cs`:
- **Тест `Redemption_DoesNotDoubleCountPrincipal`:** один holding `MarketValueRub=1000`; месячные сводки:
  месяц 1 — купон gross 50 (tax 6.5), месяц 2 — погашение `PrincipalGrossRub=1000` (tax 0). `reinvestRate=0`,
  horizon=2.
  - Ожидание (без реинвеста): `portfolioValue` мес.2 ≈ **1043.5** (купон-нетто 43.5 + тело 1000, бумага
    обнулилась), а **не** ~2043. `cumulativeIncome` мес.2 == **43.5** (только купон), не 1043.5.
  - На текущем коде получится ~2000+ → тест падает (баг воспроизведён).
- **Тест `Income_ExcludesPrincipal`** и **`CurrentMonthIncluded`** (M-2): отдельные ассерты.

### Проверка фикса
- Тесты зелёные.
- Боевая сверка (опционально, вручную через прод-API после деплоя): траектория растёт плавно ~ на ставку
  реинвеста, без вертикальных скачков на датах погашения.
- **Чеклист:** [ ] нет двойного учёта тела; [ ] доход = купоны; [ ] текущий месяц учтён; [ ] реинвест
  через корень степени; [ ] `pre-push-check` зелёный.

---

## T-4 🟠 Сценарии ставок: единая база и охват флоатеров — находки H-1, M-1

> Один баг (несогласованность базы расчёта) с двумя проявлениями. Объединено.

### Что за баг
`RateScenarioService.Compute` считает базу `currentValue` только по позициям **с** `ModifiedDuration`
(флоатеры/бумаги без дюрации выпадают), а эндпоинт отдаёт `CurrentValueRub` по **всему** портфелю. Итог:
`NewValueRub ≠ CurrentValueRub + DeltaRub` при наличии флоатеров; `DeltaPercent` берётся от подмножества,
а в UI подписан как изменение «портфеля» (вводит в заблуждение).

- **Файлы:** [src/Bonds.Core/Analytics/RateScenarioService.cs](../src/Bonds.Core/Analytics/RateScenarioService.cs); [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs) (`GetRateScenario`); фронт [Analytics.tsx](../bonds-web/src/pages/Analytics.tsx) (`RateScenarioWidget`).

### Починка (качественно)
1. В `Compute` считать базу `currentValue` по **всем** holdings (`Σ MarketValueRub`), а чувствительность —
   только у позиций с `ModifiedDuration` (у остальных вклад в Δ = 0, т.е. цена не меняется при сдвиге).
   Тогда `NewValueRub = currentValue + Σ Δ` и `DeltaPercent = Δ / currentValue` согласованы и относятся ко
   всему портфелю.
2. Отдавать в DTO признак охвата: `RateSensitiveValueRub` (сумма бумаг, у которых есть дюрация) — чтобы
   фронт честно подписал «процентно-чувствительная часть: X из Y».
3. Фронт: заменить подпись «портфель подешевеет…» на корректную, с оговоркой, что флоатеры/бумаги без
   дюрации к параллельному сдвигу малочувствительны и в Δ не входят.

### Воспроизведение (тест)
`tests/Bonds.Tests/Analytics/RateScenarioServiceTests.cs`, тест `BaseValueIncludesAllHoldings`:
- holdings: фикс (ModifiedDuration=3, Convexity=null, MarketValueRub=1000) + флоатер (ModifiedDuration=null,
  MarketValueRub=1000). Сдвиг +100 б.п.
- Ожидание: `currentValue==2000`; `deltaRub == -30` (= −3·0.01·1000, флоатер 0); `newValue == 1970`;
  инвариант `newValue == currentValue + deltaRub`; `deltaPercent == -1.5` (от 2000).
- На текущем коде база = 1000, newValue=970 → инвариант с CurrentValueRub=2000 нарушается → падает.

### Проверка фикса
- Тест зелёный; инвариант `NewValue == Current + Delta` выполняется при флоатерах.
- **Чеклист:** [ ] база = весь портфель; [ ] флоатеры в базе, но 0 в Δ; [ ] подпись на фронте честная;
  [ ] `pre-push-check` зелёный.

---

## T-5 🟡 Единое «бизнес-сегодня» (TZ) — находка M-3

### Что за баг
`PortfolioTrajectoryService`/`GetTrajectory`/`GetCashFlow` используют `DateTime.Today` (локаль сервера),
остальные эндпоинты — `DateOnly.FromDateTime(DateTime.UtcNow)`, планировщик — MSK. Рассинхрон «сегодня»
у границы суток / при ненулевой TZ контейнера сдвигает горизонты/НКД/дни-до-даты на день.

- **Файлы:** все вхождения `DateTime.Today` / `DateTime.UtcNow` для `asOf` (см. список в аудите M-3):
  [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs):344,348; [CashFlowEndpoints.cs](../src/Bonds.Api/Endpoints/CashFlowEndpoints.cs):51; positions/composition/scatter/comparison/xirr — `UtcNow`.

### Починка
1. Завести единый источник «бизнес-даты» — статический helper или `IClock` с методом `MoscowToday()`
   (MSK = `TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, "Europe/Moscow")` → `DateOnly`), по аналогии с
   [SyncSchedulerHostedService.cs](../src/Bonds.Infrastructure/Scheduling/SyncSchedulerHostedService.cs):98.
2. Заменить **все** прямые `DateTime.Today`/`DateOnly.FromDateTime(DateTime.UtcNow)` в расчётных
   эндпоинтах/сервисах на этот helper. (Метки `CreatedAt`/`UpdatedAt`/JWT exp не трогать — там UtcNow к месту.)

### Воспроизведение / Проверка
- Тест `tests/Bonds.Tests/.../BusinessClockTests.cs`: helper возвращает дату MSK (например, замокать
  фиксированный UTC `2026-06-26T23:30Z` → MSK даёт `2026-06-27`). Проверить, что все asOf берутся из него.
- Проще верифицируется грепом: после фикса в расчётных путях нет прямых `DateTime.Today`.
- **Чеклист:** [ ] один источник даты; [ ] нет `DateTime.Today` в расчётах; [ ] `pre-push-check` зелёный.

---

## T-6 🟡 Индексируемые облигации (линкеры): тело без индексации — находка M-5

> В текущем портфеле линкеров (ОФЗ-ИН) нет — задача защитная/на будущее. Низкий приоритет, не блокирует.

### Что за баг
Для `CouponType.Indexed` погашение тела проецируется по базовому `FaceValue` (1000) без индексации на
инфляцию → сумма погашения занижена/некорректна.

- **Файлы:** [CashFlowProjectionService.cs](../src/Bonds.Core/CashFlow/CashFlowProjectionService.cs):84; [BondCashFlowBuilder.cs](../src/Bonds.Core/Calculation/BondCashFlowBuilder.cs):44,71; [BondSyncService.cs](../src/Bonds.Infrastructure/Sync/BondSyncService.cs) (обновление FaceValue).

### Починка
- MOEX `FACEVALUE` для ОФЗ-ИН отражает **текущий проиндексированный** номинал — убедиться, что
  `EnrichFromMoexAsync` регулярно обновляет `FaceValue` из MOEX (а не фиксирует базовый 1000).
- Будущее тело линкера всё равно неизвестно точно (зависит от будущей инфляции) → помечать поток
  погашения линкера `IsEstimated=true` (уже помечается, проверить) и в UI оговаривать оценочность.
- Не пытаться моделировать инфляцию (нет бесплатного источника, §3).

### Воспроизведение / Проверка
- Тест `tests/Bonds.Tests/CashFlow/...`: для `CouponType.Indexed` поток погашения помечен `IsEstimated`,
  а `FaceValue` берётся из обновлённого значения (мок MOEX с FACEVALUE > 1000 → погашение использует его).
- **Чеклист:** [ ] FaceValue линкера обновляется из MOEX; [ ] погашение помечено оценочным; [ ] `pre-push-check` зелёный.

---

## T-7 ⚪ Согласовать дюрацию: scatter ↔ G-спред — находки L-1, L-2

> Один баг (какую дюрацию использовать как «срок» для кривой и оси). Объединено L-1+L-2.

### Что за баг
Scatter наносит точки по **модифицированной** дюрации (X), а G-спред считает по дюрации **Маколея**
(`GSpreadCalculator.GSpread(ytm, macaulay, curve)`). Поэтому визуальное «над/под кривой» не совпадает со
знаком G-спреда. Doc-comment у `GSpread` тоже говорит «модифицированная», а используется Маколей.

- **Файлы:** [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs) (scatter DTO/маппинг); [Analytics.tsx](../bonds-web/src/pages/Analytics.tsx) (`ScatterWidget`); [BondMetricsCalculator.cs](../src/Bonds.Core/Calculation/BondMetricsCalculator.cs):162 (вызов GSpread); [GSpreadCalculator.cs](../src/Bonds.Core/Calculation/GSpreadCalculator.cs) (doc).

### Починка
- Выбрать **один** измеритель срока для обоих мест — рекомендую дюрацию **Маколея** (это срок-к-потокам,
  естественный аргумент для кривой). Тогда: G-спред уже на Маколее — оставить; scatter рисовать по
  Маколею: добавить `macaulayDuration` в `ScatterPointDto` и строить ось X по нему (подпись «Дюрация
  Маколея, лет»). Привести doc-comment `GSpread` в соответствие («дюрация Маколея»).
- Альтернатива (если проще) — везде модифицированная: тогда поменять вызов `GSpread(..., modified, ...)`.
  Главное — единый выбор.

### Воспроизведение / Проверка
- Фронт-тест `Analytics.test.tsx`: точка с известными дюрацией/доходностью лежит относительно кривой
  консистентно со знаком G-спреда из того же ответа.
- **Чеклист:** [ ] один измеритель срока; [ ] подпись оси обновлена; [ ] doc-comment поправлен; [ ] тесты зелёные.

---

## T-8 ⚪ Текущая доходность при нерегулярных купонах — находка L-3

### Что за баг
`CurrentYieldCalculator` берёт один «текущий» купон и аннуализирует `×365/periodDays`; при нерегулярном
первом/последнем периоде оценка ставки «гуляет».

- **Файл:** [CurrentYieldCalculator.cs](../src/Bonds.Core/Calculation/CurrentYieldCalculator.cs):30–40.

### Починка
- Аннуализировать по устойчивой оценке частоты (использовать `CouponFrequencyEstimator.EstimateCouponsPerYear`
  как основной множитель: `annualCoupon = currentCoupon * k`), а `periodDays` — только как фолбэк. Игнорировать
  аномально короткие/длинные крайние периоды (например, брать медианный период вместо крайнего).

### Воспроизведение / Проверка
- Тест `tests/Bonds.Tests/Calculation/CurrentYieldCalculatorTests.cs`: график с коротким первым купоном —
  текущая доходность близка к «нормальной» (в пределах ~5% относительной погрешности), а не задвоена/занижена.
- **Чеклист:** [ ] устойчиво к нерегулярным периодам; [ ] `pre-push-check` зелёный.

---

## T-9 ⚪ Согласовать нормировку выпуклости и PVBP — находка L-4

### Что за баг
В `DurationCalculator` выпуклость нормируется на `Σ PV`, а PVBP — на переданный `DirtyPrice`; при неточном
YTM (`pvSum ≠ price`) метрики чуть рассинхронизированы.

- **Файл:** [DurationCalculator.cs](../src/Bonds.Core/Calculation/DurationCalculator.cs):62–65.

### Починка
- Привести к единой базе: использовать `dirtyPrice` (рыночную грязную цену) как знаменатель и для
  Маколея/выпуклости, и для PVBP — это рыночно-консистентно. Либо явно задокументировать, почему база
  разная. Эффект мал, но устранить рассинхрон стоит.

### Воспроизведение / Проверка
- Тест: при `pvSum != dirtyPrice` (искусственно) метрики считаются от одной базы; на бумаге у YTM PVBP =
  modDur·dirtyPrice·1e-4 точно.
- **Чеклист:** [ ] единая база; [ ] существующие тесты дюрации зелёные.

---

## T-10 ⚪ Анализ замены: задокументировать упрощение — находка L-5

> Скорее «документировать», чем «чинить» — текущая линейная оценка приемлема для MVP. Низший приоритет.

### Что
`SwitchAnalysisService` оценивает выгоду линейно (`спред × горизонт`) и применяет спред к полной стоимости
hold-позиции, хотя в target идёт меньший капитал после комиссии.

- **Файл:** [SwitchAnalysisService.cs](../src/Bonds.Core/Analytics/SwitchAnalysisService.cs):54–55.

### Починка (минимум)
- Уточнить дисклеймер/`Note`, что оценка линейная и без компаундирования, и что комиссия учтена один раз.
- Опционально (если есть время): считать `spreadGain` на капитале **после** комиссии продажи
  (`netProceedsAfterSale`), а не на полной стоимости. Тест на знак/монотонность.

### Проверка
- **Чеклист:** [ ] дисклеймер уточнён; [ ] (опц.) база спреда = капитал после комиссии; [ ] тесты зелёные.

---

## Definition of Done всего этапа
- [ ] T-1…T-4 (критичные/высокие) исправлены и покрыты тестами; красный→зелёный показан.
- [ ] T-5…T-10 — по приоритету; низкоприоритетные задачи можно отдельными PR.
- [ ] Каждая правка — отдельный коммит с понятным сообщением; перед каждым пушем — `pre-push-check` зелёный.
- [ ] После деплоя — выборочная сверка на проде (`https://mvv42.ru/bonds/api`): у многокупонных бумаг YTM
      стал вменяемым (нет отрицательных), траектория без скачков, валютные бумаги помечены.
