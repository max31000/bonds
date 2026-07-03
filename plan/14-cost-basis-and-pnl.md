# Задача 14 — Цена входа, P&L и честная подпись доходности

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Общие конвенции —
> [CLAUDE.md](../CLAUDE.md), обзор — [00-overview-and-architecture.md](00-overview-and-architecture.md).
>
> **Зависимости:** нет (независима от задачи 13, можно делать параллельно).
>
> **Конвенция единиц (не нарушать):** доходности на бэкенде — доли (0.12 = 12%), проценты делает
> форматтер фронта ([format.ts](../bonds-web/src/utils/format.ts)). Рубли — `decimal`. Перед пушем —
> скилл `pre-push-check` на зелёном.

---

## Проблема

Пользователь смотрит на колонку «Доходность» в таблице позиций и не понимает, **от какой цены** она
посчитана. Ответ: YTM и текущая доходность считаются от текущей рыночной (грязной) цены — это
корректно, но нигде не написано. А метрик «от цены входа» (сколько я реально заработал на этой
бумаге) в продукте нет вообще, хотя журнал операций с фактическими суммами покупок/продаж/купонов
лежит в таблице `operations` (модель [Operation.cs](../src/Bonds.Core/Models/Operation.cs),
репозиторий [OperationRepository.cs](../src/Bonds.Infrastructure/Repositories/OperationRepository.cs)).

## Что сделать

### A. Расчёт cost basis по позиции (Bonds.Core, чистая математика + тесты)

Новый чистый сервис `Bonds.Core/Analytics/PositionCostBasisService.cs` (статический, без I/O,
по образцу [PortfolioXirrService.cs](../src/Bonds.Core/Analytics/PortfolioXirrService.cs)):

Вход: журнал `Operation` по одному инструменту + текущее количество и текущая рыночная стоимость.
Выход (record `PositionCostBasis`):
- `AverageCostRub` — средняя цена входа за бумагу (метод: **average cost**, средняя взвешенная по
  всем покупкам с учётом частичных продаж; FIFO не делаем — брокерская отчётность Т-Инвестиций для
  НДФЛ использует FIFO, но для дисплейной метрики средняя проще и понятнее; зафиксировать выбор в
  doc-comment);
- `InvestedRub` — вложено в текущий остаток (средняя × количество);
- `UnrealizedPnlRub` / `UnrealizedPnlPercent` — текущая стоимость минус вложенное (доля!);
- `CouponsReceivedRub` — сумма купонных операций по бумаге (за вычетом налоговых операций,
  относящихся к купонам, если они мапятся отдельным типом — см.
  [TInvestOperationMapper.cs](../src/Bonds.Infrastructure/Connectors/TInvest/TInvestOperationMapper.cs));
- `TotalReturnRub` / `TotalReturnPercent` — P&L + купоны, относительно вложенного;
- `HasUnknownLots` — флаг «журнал неполон» (количество из операций не сходится с текущей позицией —
  например, бумага куплена до начала истории операций). При флаге метрики отдавать, но фронт покажет
  пометку.

**Знаки:** `Operation.AmountRub` уже приходит со знаком от брокера — не перезаписывать
(см. длинный doc-comment в `PortfolioXirrService.SignedAmount` почему).

**Тесты** (`tests/Bonds.Tests/Analytics/PositionCostBasisServiceTests.cs`): покупка в два лота по
разным ценам → средняя; частичная продажа не меняет среднюю, но уменьшает вложенное; купоны
суммируются; кейс «продано больше, чем куплено в журнале» → `HasUnknownLots`; пустой журнал.

### B. Прокинуть в API

`GET /api/positions` ([PositionsEndpoints.cs](../src/Bonds.Api/Endpoints/PositionsEndpoints.cs)) —
расширить строку позиции полями: `averageCostRub`, `investedRub`, `unrealizedPnlRub`,
`unrealizedPnlPercent`, `couponsReceivedRub`, `totalReturnPercent`, `costBasisIncomplete`
(nullable там, где посчитать нельзя). Журнал операций по инструментам одного счёта читать одним
запросом, не N+1 (посмотри, как `PortfolioHoldingsBuilder`
([PortfolioHoldingsBuilder.cs](../src/Bonds.Infrastructure/Analytics/PortfolioHoldingsBuilder.cs))
собирает данные — возможно, логично встроить расчёт туда же).

Интеграционный тест: засеять операции + позицию, дёрнуть `/api/positions`, проверить числа.

### C. Показать во фронте

[Positions.tsx](../bonds-web/src/pages/Positions.tsx) + [types.ts](../bonds-web/src/api/types.ts):

1. Новые колонки: «Ср. цена входа», «P&L» (₽ и % в одной ячейке, зелёный/красный цвет),
   «Купоны получено». Полная доходность (`totalReturnPercent`) — tooltip'ом в ячейке P&L, чтобы не
   раздувать таблицу. При `costBasisIncomplete` — серый бейдж «журнал неполон» в «Пометках».
2. Таблица уже широкая (`minWidth={900}`) — поднять `minWidth` и/или спрятать колонки «Сектор» и
   «Тип купона» в существующие «Пометки»/tooltip, на твоё усмотрение, но горизонтальный скролл
   не должен появляться на 1440px.
3. **Честная подпись доходности:** заголовок колонки «Доходность» дополнить иконкой «?»
   (Mantine `Tooltip` + `ActionIcon`) с текстом: «YTM — эффективная доходность к погашению/оферте
   от **текущей** рыночной цены. Не зависит от вашей цены покупки. Доход от цены входа — колонка P&L».
4. Сортировку разрешить по обеим колонкам (доходность и P&L%) — текущий toggle-паттерн
   `sortDirection` обобщить до `{ key, direction }`.

Vitest-тесты: рендер колонок, цвет P&L, бейдж «журнал неполон», сортировка по P&L.

## Критерии приёмки

- [ ] Юнит-тесты cost basis покрывают среднюю цену, частичную продажу, купоны, неполный журнал.
- [ ] `/api/positions` отдаёт новые поля без N+1 (один батч-запрос операций).
- [ ] В таблице видны цена входа, P&L ₽/%, купоны; у «Доходности» — объясняющий tooltip.
- [ ] Сортировка работает по доходности и по P&L.
- [ ] `dotnet test`, `yarn typecheck`/`test:run`/`build` зелёные; `pre-push-check` пройден.
