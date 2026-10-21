# Задача 52 — Единый журнал доходов: P&L по каждой позиции (включая закрытые), API статистики

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (паттерны и контракт единиц — читать обязательно).
>
> **Зависимости:** 51 (таблицы/репозитории `funds`, `fund_positions`, `fund_operations`).
> **Порядок в волне:** строго после 51; до 53 (53 переиспользует калькулятор этой задачи) и 54 (UI).
>
> **Рамки (что НЕ делать):** не менять существующие `PositionCostBasisService`, `IfSoldNowService`,
> `PortfolioXirrService`, снимки/бэкфилл и их DTO — новый сервис живёт рядом (существующие экраны
> продолжают работать как есть). Без FIFO и без налогового модуля. Без UI (задача 54).

---

## Проблема

Пользователь не может понять, сколько он реально заработал по облигациям: в приложении брокера
выплаченные купоны плохо учитываются в статистике. В продукте сейчас:
- `PositionCostBasisService` считает P&L только по ОТКРЫТОЙ позиции (при qty = 0 — null), не
  считает реализованную прибыль от продаж, игнорирует `Redemption`/`Fee`.
- Закрытые/погашенные позиции удаляются сверкой (`PositionRepository.DeleteAllExceptAsync`), их
  история нигде не видна, хотя операции в журнале остаются (`operations.instrument_id` сохраняется).
- **НКД смешан с купонами:** покупка облигации оплачивает продавцу НКД (сумма операции = чистая
  цена + НКД), первый купон его возвращает — «полученные купоны» завышены на уплаченный НКД, а цена
  входа — завышена на него же. Брокер отдаёт НКД операции полем `AccruedInt`
  (`TInvestOperation.AccruedInterest` уже парсится в `TInvestPortfolioClient` ~:379), но в БД не пишется.
- Нет общей картины: доход по месяцам, вклад купонов/продаж/налогов/комиссий, XIRR всего портфеля
  с фондами.

## Что сделать

### A. НКД операций — миграция `024_operations_accrued.sql`

1. `ALTER TABLE operations ADD COLUMN accrued_rub DECIMAL(20,6) NULL` — **НКД операции, РУБЛИ на всю
   операцию, ВЕЛИЧИНА (≥ 0)**; направление выводится из типа (Buy — уплачен, Sell — получен).
   NULL = брокер не отдал/старая строка. Обновить `MigrationIdempotencyTests`.
2. `Operation.AccruedRub` (decimal?), `OperationRepository`: колонка в SELECT/INSERT и в
   `ON DUPLICATE KEY UPDATE` (журнал перечитывается целиком каждым синком — существующие строки
   заполнятся на ближайшем синке; это и есть «бэкфилл»). `BondSyncService` пишет
   `Math.Abs(op.AccruedInterest)` для Buy/Sell (для прочих типов — null).
3. Допущение (зафиксировать doc-comment'ом на `AccruedRub` и в README коннектора): `Payment`
   покупки/продажи облигации у T-Invest ВКЛЮЧАЕТ НКД. Защита от обратного: если для операции
   `accrued_rub ≥ |amount_rub|` — считать НКД неизвестным (калькулятор ниже не вычитает его).

### B. Единый журнал (Core, чистые функции)

1. `Bonds.Core/Income/LedgerEntry.cs`:
   `AssetKind { Bond, Fund }`, `LedgerEntryKind { Buy, Sell, Income, Tax, Fee, PrincipalReturn, Redemption, SecurityIn, SecurityOut }`,
   `LedgerEntry(AssetKind, ulong AssetId, DateTime Date, LedgerEntryKind Kind, decimal AmountRub /*знак брокера*/, decimal? Quantity, decimal? AccruedRub /*величина*/, ulong SourceId)`.
   Маппинг: облигации — Buy/Sell/Coupon→Income/Amortization→PrincipalReturn/Redemption/Tax/Fee/SecurityIn/Out;
   фонды — Buy/Sell/Distribution→Income/Tax/Fee/SecurityIn/Out. Операции без актива (instrument_id/fund_id NULL)
   — «по счёту» (не в позициях, отдельной строкой в итогах).
2. `Bonds.Core/Income/PositionIncomeCalculator.cs` — по журналу ОДНОГО актива (+ текущее количество,
   текущая ЧИСТАЯ стоимость остатка, текущий НКД остатка, asOf). Метод — **average cost** (как
   `PositionCostBasisService`; doc-comment почему не FIFO можно сослать туда). Правила:
   - Buy: `cleanCost = |amount| − accruedPaid` (если НКД известен и `< |amount|`, иначе весь amount в
     cost и флаг `AccruedUnknown`); `qty += q`; `costRemaining += cleanCost`; `investedTotal += |amount|`;
     `accruedPaid += accrued`.
   - Sell: `proceedsClean = amount − accruedReceived` (то же правило известности НКД);
     `realized += proceedsClean − avgCost × q`; `accruedReceived += accrued`; продано больше
     известного остатка → клампим + `HasUnknownLots` (как в существующем сервисе).
   - PrincipalReturn (амортизация): `costRemaining −= amount`; если уходит ниже нуля — избыток в
     `realized`, cost = 0. `principalReturned += amount`.
   - Redemption: `realized += amount − costRemaining`; qty → 0, cost → 0. `principalReturned += amount`.
   - Income: `incomeGross += amount` (купон/выплата фонда брутто). Tax: `taxes += amount` (знак
     брокера: удержание −, возврат +). Fee: `fees += amount` (обычно −).
   - SecurityIn/Out: как в существующем сервисе (лот без цены → `HasUnknownLots`, количество меняется).
   - Итог по количеству журнала ≠ фактическое количество → `HasUnknownLots`.
   Выход `PositionIncome`:
   `Status (Open|Closed|Redeemed)`, `FirstDate`, `LastDate`, `HoldingDays`, `QuantityNow`,
   `InvestedTotalRub` (Σ|покупок| с НКД), `CostRemainingRub`, `MarketValueCleanRub`, `AccruedNowRub`,
   `UnrealizedPnlRub` (= MarketValueClean − CostRemaining; null у закрытой), `RealizedPnlRub`,
   `IncomeGrossRub`, `AccruedPaidRub`, `AccruedReceivedRub`,
   `NetCouponIncomeRub` (= IncomeGross − AccruedPaid + AccruedReceived + AccruedNow — «честный
   купонный доход»: уплаченный продавцу НКД вычтен, накопленный — добавлен), `TaxesRub`, `FeesRub`,
   `PrincipalReturnedRub`, `TotalResultRub`, `TotalReturnFraction` (= TotalResult / InvestedTotal,
   ДОЛЯ), `XirrFraction` (через существующий `XirrCalculator`; null если < 30 дней владения или не
   сошлось), `HasUnknownLots`, `AccruedUnknown`.
   **Инвариант (закрепить тестом на каждом эталоне):**
   `TotalResultRub == Σ AmountRub(все операции актива) + MarketValueClean + AccruedNow`
   и `TotalResult == Realized + (Unrealized ?? 0) + NetCouponIncome + Taxes + Fees`.
3. `Bonds.Core/Income/IncomeStatisticsService.cs` — агрегирует набор `PositionIncome` + журнал:
   - Итоги all-time: суммы полей по позициям + «по счёту» (налоги/комиссии без актива).
   - Период `[from, to]` (даты, включительно): **денежные события периода** — купоны/выплаты брутто,
     налоги, комиссии, реализованный результат (атрибутируется дате продажи/погашения), НКД уплачен/
     получен. Бумажная прибыль в периоде НЕ считается (нужна оценка на начало периода — вне скоупа);
     это подписывается в DTO (`unrealizedIsAllTime: true`).
   - Помесячный ряд (`yyyy-MM`) по периоду: `couponsGrossRub`, `distributionsGrossRub`, `accruedNetRub`
     (−уплачен + получен), `taxesRub`, `feesRub`, `realizedPnlRub`, `netRub` (сумма). Месяцы без
     событий внутри периода — нулевые строки (ровная ось графика).
   - XIRR портфеля (облигации + фонды): все операции с активом + «по счёту» налоги/комиссии +
     терминальный поток = текущая стоимость облигаций (грязная) + фондов. Деньги на счёте и
     пополнения не участвуют (как у существующего XIRR) — подписать.

### C. Сборка входов и API (Infrastructure/Api)

1. `IncomeLedgerBuilder` (`Bonds.Infrastructure/Analytics`): читает `operations` + `fund_operations`
   счёта одним запросом каждое, текущие облигационные позиции через `PortfolioHoldingsBuilder`
   (грязная стоимость → чистая = MarketValue − AccruedPerBond × Qty; НКД остатка = AccruedPerBond × Qty —
   единое правило стоимости, не пересчитывать цену самому), фонды — `fund_positions`
   (`current_price_rub × quantity`, НКД 0). Имена: облигации — `instruments.name/isin`, фонды —
   `funds.name/ticker`. Сбой расчёта одного актива → он помечается `dataIncomplete`, остальные считаются.
2. `IncomeEndpoints` (регистрация в `Program.cs`, авторизация глобальная):
   - `GET /api/income?from=&to=&assetKind=all|bond|fund&includeClosed=true` → `IncomeResponseDto`:
     `{ asOf, from, to, assetKind, totals: IncomeTotalsDto, accountLevel: { taxesRub, feesRub },
       monthly: IncomeMonthDto[], positions: IncomePositionDto[], portfolioXirrFraction?, unrealizedIsAllTime,
       disclaimer }`. `from/to` пусты → всё время. Валидация (from > to, неизвестный assetKind) → 422 по
     образцу `AnalyticsSupport`. `IncomePositionDto` = поля `PositionIncome` + `assetKind`, `assetId`,
     `name`, `ticker|isin`, `positionId?` (для ссылки на карточку облигации), `fundId?`, плюс
     периодные поля (`periodIncomeGrossRub`, `periodRealizedPnlRub`, `periodTaxesRub`, `periodFeesRub`).
   - `GET /api/income/{assetKind}/{assetId}/ledger` → хронологический список операций актива с
     нарастающими `quantityAfter`, `avgCostAfterRub`, `realizedPnlRub` (на этой операции), `accruedRub`,
     `kind`, `amountRub`, `date` — для раскрытия строки на фронте. 404 если актива нет в журнале счёта.
   - DTO — `Endpoints/Dtos/IncomeDtos.cs`; все доли — ДОЛИ, суммы — рубли.
3. Дисклеймер в ответе: «оценка по журналу брокера; налоги — как удержал брокер; метод средней цены,
   не FIFO — может отличаться от налоговой отчётности».
4. Обновить `docs/CODEBASE-GUIDE.md`: раздел «Доходы (задача 52)» — правила калькулятора, инвариант,
   единицы `accrued_rub`, что периодная статистика — денежные события.

### D. Тесты

- Юнит `PositionIncomeCalculator` (эталоны руками в комментариях):
  1. Купил 10 бумаг по 1 000 + НКД 20 на бумагу (amount −10 200, accrued 200), купон 400 брутто,
     налог −52, держу, чистая стоимость 10 100, НКД сейчас 50 → NetCouponIncome = 400 − 200 + 50 = 250,
     Unrealized = 100, Taxes −52, TotalResult = 298; инвариант с Σ amounts (−10 200 + 400 − 52) + 10 150 = 298.
  2. Покупка двумя лотами (5×990, 5×1 010), продажа 4 по 1 030 (НКД получен 40) → realized по средней 1 000.
  3. Амортизация 300 при cost 10 000 → cost 9 700, realized 0; погашение 9 800 → realized 100, статус Redeemed.
  4. НКД неизвестен (null) → `AccruedUnknown`, cost включает весь amount, инвариант держится.
  5. accrued ≥ |amount| → НКД игнорируется.
  6. Продажа больше известного остатка → HasUnknownLots, без отрицательного остатка.
  7. Фонд: покупка 100 паёв по 100, три выплаты по 120, налог −46.8, цена 101 → итог и инвариант.
- Юнит `IncomeStatisticsService`: период режет события по датам включительно; реализованный
  результат попадает в месяц продажи; пустые месяцы заполнены; XIRR портфеля на эталоне
  (сверить с `XirrCalculator` на тех же потоках); операции «по счёту» в итогах, не в позициях.
- Интеграционные: `/api/income` на засеянных облигации (в т.ч. закрытой — без строки в `positions`) +
  фонде; фильтры assetKind/period; 422 на from > to; `/ledger` по закрытой облигации; 401 без auth
  (добавить маршруты в список 401-теории); `accrued_rub` апдейтится повторным upsert'ом.

## Критерии приёмки

- [ ] `/api/income` показывает каждую позицию, включая проданные/погашенные, с реализованной и
      бумажной прибылью, купонами/выплатами брутто/налог/нетто, НКД уплаченным/полученным, комиссиями,
      итогом ₽/доля и XIRR.
- [ ] Инвариант «итог = Σ денежных потоков + текущая стоимость» выполняется для каждой позиции (тесты).
- [ ] Помесячный ряд и периодные итоги — по денежным событиям периода; бумажная прибыль явно
      помечена как all-time.
- [ ] Существующие эндпоинты/DTO и их тесты не изменились (кроме добавления `accrued_rub` в журнал).
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
