# Задача 24 — НКД первым классом в UI

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Конвенции — [CLAUDE.md](../CLAUDE.md).
> Рубли — decimal. Перед пушем — `pre-push-check` (пуш делает оркестратор).
>
> **Зависимости:** нет жёстких (задача 23 переписывает секцию «Замены» — её карточки НЕ трогай,
> твоя зона в рекомендациях — только секция аллокации).

---

## Проблема

НКД везде УЧТЁН, но нигде не ПОКАЗАН. Факты (проверены аудитом): рыночная стоимость позиции =
грязная цена × кол-во; аллокация считает цену лота как чистая + НКД + комиссия
([CashAllocationService.cs](../src/Bonds.Core/Analytics/CashAllocationService.cs), см. doc-comment
`EstimatedCostRub`); «если продать» = грязная стоимость − комиссия
([IfSoldNowService.cs](../src/Bonds.Core/Analytics/IfSoldNowService.cs)); YTM — от грязной цены.
Пользователь, зная механику Т-Инвестиций («продаёшь — получаешь НКД, покупаешь — платишь НКД»),
не может проверить, учтено ли это — ни одна цифра в UI не разложена.

## Что сделать

### A. НКД в DTO позиций

1. **Сначала проверь семантику** `Position.Accrued`
   ([Position.cs](../src/Bonds.Core/Models/Position.cs), пишется в
   [BondSyncService.cs](../src/Bonds.Infrastructure/Sync/BondSyncService.cs) из `tip.CurrentNkd`) —
   НКД на ОДНУ бумагу или на позицию целиком? T-Invest `PortfolioPosition.current_nkd` — на одну
   бумагу; подтверди по коду (как `PortfolioHoldingsBuilder` использует его при расчёте
   MarketValueRub: если dirty = clean + Accrued и потом × Quantity — значит на бумагу).
   Зафиксируй doc-comment'ом на `Position.Accrued`.
2. `GET /api/positions` ([PositionsEndpoints.cs](../src/Bonds.Api/Endpoints/PositionsEndpoints.cs)):
   добавить в строку `accruedPerBondRub` и `accruedTotalRub` (= × количество). Аналогично в
   детальный DTO `/api/positions/{id}` (если там уже есть НКД-метрика — переиспользуй, не дублируй).

### B. Показать в UI

1. **Таблица позиций** ([Positions.tsx](../bonds-web/src/pages/Positions.tsx)): в ячейке
   «Рыночная стоимость» под суммой — серым мелким «в т.ч. НКД {accruedTotalRub}» (только если > 0).
   НЕ сломай live-ячейку (LiveMarketValueCell), heatmap, строку «Итого», мобильные карточки —
   в мобильной карточке НКД в раскрывашку. В строку «Итого» добавь суммарный НКД портфеля той же
   серой подписью.
2. **Аллокация** (секция «Куда вложить сумму» в
   [Recommendations.tsx](../bonds-web/src/pages/Recommendations.tsx)): бэкенд — в
   `AllocationCandidateDto` добавить разбивку `cleanPriceRub` / `accruedRub` / `commissionRub`
   (данные в `CashAllocationService` уже есть на входе — прокинь, не пересчитывай); фронт — в
   строке результата: «купить 4 шт × 1 046 ₽ (цена 1 012 + НКД 34 + комиссия 0,5)» + однажды на
   секцию подпись «уплаченный при покупке НКД вернётся ближайшим купоном».
3. **«Если продать сейчас»** ([PositionDetail.tsx](../bonds-web/src/pages/PositionDetail.tsx)):
   в DTO `ifSoldNow` добавить `cleanValueRub`/`accruedTotalRub` (разложение MarketValueRub);
   карточка показывает: «выручка = чистая стоимость X + НКД Y − комиссия Z = N ₽».
4. **Глобальная подсказка**: у «Итого» таблицы и у KPI «стоимость портфеля» на дашборде
   ([Dashboard.tsx](../bonds-web/src/pages/Dashboard.tsx)) — tooltip/«?»: «Все стоимости — „грязные“:
   включают накопленный купонный доход (НКД). Так же считает брокер при продаже/покупке».

### C. Тесты

Юнит/интеграционные на новые поля DTO (accrued per bond/total, разбивка аллокации, разбивка
ifSoldNow — суммы сходятся: clean + accrued − commission = netProceeds). Vitest: подпись «в т.ч.
НКД» рендерится и скрывается при 0; разбивка в аллокации; формула в ifSoldNow.

## Критерии приёмки

- [ ] Семантика `Position.Accrued` подтверждена и задокументирована; DTO позиций несут НКД.
- [ ] НКД виден: в таблице (и «Итого»), в аллокации (разбивка цены лота), в «если продать» (формула).
- [ ] Глобальная подсказка про грязные цены на дашборде и у «Итого».
- [ ] Ничего из задач 14/16/21 в таблице не сломано (cost basis, live-ячейка, heatmap, мобилка).
- [ ] `dotnet build/test`, `yarn typecheck/test:run/build/lint` — зелёные.
