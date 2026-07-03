# Задача 16 — Живые котировки: автообновление таблицы и интрадей-график

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Общие конвенции —
> [CLAUDE.md](../CLAUDE.md), обзор — [00-overview-and-architecture.md](00-overview-and-architecture.md).
>
> **Зависимости:** задача 13 (живой токен T-Invest) — без неё котировки не пойдут; кодовые
> пересечения только в AppLayout (статус в шапке). Дашборд (задача 18) потребляет эндпоинты из этой
> задачи, поэтому 16 желательно делать раньше 18.
>
> **Рамки:** НЕ использовать gRPC-стриминг `MarketDataStream` и SignalR — осознанное решение:
> сервис single-user, поллинг раз в 30–60 сек полностью закрывает потребность и на порядок проще
> в эксплуатации. Точку расширения оставить (интерфейс), реализацию — нет. Перед пушем —
> `pre-push-check` на зелёном.

---

## Проблема

Данные обновляются 2 раза в день по расписанию
([SchedulerOptions.cs](../src/Bonds.Infrastructure/Scheduling/SchedulerOptions.cs)) или кнопкой.
Пользователь хочет: открыл вкладку — стоимость портфеля и позиций живёт сама, плюс график стоимости
внутри дня. Полный синк для этого гонять нельзя (тяжёлый: MOEX-справочник, пересчёт метрик,
сигналы) — нужен лёгкий контур «только цены».

## Что сделать

### A. Лёгкий контур котировок (backend)

1. Новый hosted service `Bonds.Infrastructure/Quotes/LiveQuotesPollingService.cs`:
   - работает только в торговые часы MOEX (будни ~09:50–19:00 МСК; вынести окно в options
     `LiveQuotesOptions` рядом с [SchedulerOptions.cs](../src/Bonds.Infrastructure/Scheduling/SchedulerOptions.cs));
   - раз в `PollingInterval` (дефолт 60 сек) берёт список открытых позиций и дёргает существующий
     `ITInvestPortfolioClient.GetQuotesAsync(figis)`
     ([ITInvestPortfolioClient.cs](../src/Bonds.Infrastructure/Connectors/TInvest/ITInvestPortfolioClient.cs));
   - складывает тики в новую таблицу `intraday_quotes` (миграция SQL как EmbeddedResource по образцу
     существующих в `Bonds.Infrastructure`; колонки: `instrument_id`, `ts_utc`, `dirty_price_rub`;
     retention — при каждой записи удалять строки старше 8 дней);
   - «нет токена/не торговые часы/ошибка сети» — молча пропустить итерацию (лог Warning), не падать.
2. Эндпоинты в новом файле `Bonds.Api/Endpoints/LiveEndpoints.cs`:
   - `GET /api/live/positions` — лёгкий ответ: `[{ positionId, instrumentId, lastPriceRub,
     marketValueRub, changeDayPercent }]` + `totalMarketValueRub`, `asOfUtc`. Считается из последнего
     тика на позицию (fallback — цена из последнего полного синка, тогда `isStale: true`);
   - `GET /api/live/portfolio-intraday?range=1d|5d` — ряд `(tsUtc, totalMarketValueRub)`,
     агрегированный по тикам (на каждый момент — сумма последних известных цен всех позиций;
     дырки — forward fill). Считать в чистом сервисе `Bonds.Core/Analytics/IntradaySeriesBuilder.cs`
     с юнит-тестами (сборка суммарного ряда из разреженных тиков по инструментам — там легко ошибиться).
3. Интеграционный тест: засеять тики двух инструментов со сдвинутыми временами → `/api/live/portfolio-intraday`
   возвращает forward-filled сумму.

### B. Живой фронт

1. Новый hook `bonds-web/src/hooks/useLiveQuotes.ts`: поллит `/api/live/positions` раз в 60 сек
   **только когда вкладка видима** (`document.visibilityState`, событие `visibilitychange`) и только
   в торговые часы (грубая проверка на клиенте, чтобы не спамить ночью). Результат — в новый
   zustand-store `useLiveStore.ts`.
2. [Positions.tsx](../bonds-web/src/pages/Positions.tsx): рыночная стоимость и изменение за день
   берутся из live-store поверх данных `/api/positions` (merge по `positionId`); ячейка мягко
   подсвечивается (CSS-transition) при изменении значения; при `isStale` — пометка «цены на
   {время синка}».
3. Интрадей-виджет «Стоимость портфеля сегодня» — новый компонент
   `bonds-web/src/components/PortfolioIntradayChart.tsx` (area-график, переключатель 1д/5д, тултип
   с временем и суммой; ось Y не от нуля — от min/max с отступом, иначе линия будет плоской).
   Разместить над таблицей позиций; задача 18 переиспользует его на дашборде.
4. Автосинк при открытии: в [AppLayout.tsx](../bonds-web/src/components/AppLayout.tsx) при маунте,
   если `GET /api/sync/status` показывает `LastSuccessAtUtc` старше 12 часов и синк не бежит —
   тихо дёрнуть `POST /api/sync` (existing [useSyncStore.ts](../bonds-web/src/store/useSyncStore.ts)),
   без модалок, с ненавязчивой нотификацией по завершении.
5. Vitest: hook останавливает поллинг при скрытой вкладке (fake timers); merge live-цен в таблицу;
   «12 часов» триггерит фоновый синк.

## Критерии приёмки

- [ ] Поллинг тиков работает только в торговые часы, retention 8 дней, ошибки не валят сервис.
- [ ] `/api/live/positions` и `/api/live/portfolio-intraday` отдают корректные данные
      (интеграционный тест + юнит-тесты `IntradaySeriesBuilder`).
- [ ] Таблица позиций обновляется сама раз в минуту при открытой вкладке, с подсветкой изменений.
- [ ] Интрадей-график стоимости портфеля живёт над таблицей (1д/5д).
- [ ] Открытие приложения после долгого перерыва запускает фоновый синк.
- [ ] `dotnet test`, `yarn typecheck`/`test:run`/`build` зелёные; `pre-push-check` пройден.
