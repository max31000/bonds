# Задача 15 — Оживить XIRR: ретроспективный бэкфилл истории

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Общие конвенции —
> [CLAUDE.md](../CLAUDE.md), обзор — [00-overview-and-architecture.md](00-overview-and-architecture.md).
>
> **Зависимости:** логически после задачи 13 (без живого токена синк не работает), но кодовые
> пересечения минимальны — можно делать параллельно. Виджет XIRR также трогает задача 18 (chart-kit) —
> если она уже выполнена, используй её общие компоненты графиков; если нет — обычный Recharts.
>
> **Конвенция единиц:** доходности — доли (0.12 = 12%). Перед пушем — `pre-push-check` на зелёном.

---

## Проблема

Виджет «Доходность портфеля (XIRR)» ([Analytics.tsx](../bonds-web/src/pages/Analytics.tsx),
`XirrWidget`) показывает пустоту с текстом «история копится с первого синка». История берётся из
снапшотов (`portfolio_value_snapshots`,
[PortfolioValueSnapshotRepository.cs](../src/Bonds.Infrastructure/Repositories/PortfolioValueSnapshotRepository.cs)),
которые пишет только автосинк 2 раза в день — а он месяцами молча падал из-за протухшего токена
(задача 13). Даже когда синк починен, график наполнится только через недели.

При этом всё для ретроспективы уже есть: журнал операций хранится с датами, а исторические цены
бумаг отдаёт MOEX ISS (`/iss/history/engines/stock/markets/bonds/securities/{secid}.json` — дневные
свечи, легальный бесплатный источник, уже используемый в проекте для справочника —
[MoexIssClient.cs](../src/Bonds.Infrastructure/Connectors/Moex/MoexIssClient.cs)).

## Что сделать

### A. Исторические цены из MOEX ISS (Infrastructure)

1. Расширить `IMoexIssClient`/`MoexIssClient` методом
   `GetHistoryPricesAsync(string secid, DateOnly from, DateOnly to, CancellationToken ct)` →
   список `(DateOnly Date, decimal? ClosePricePercent, decimal? AccruedInterestRub)` (колонки ISS:
   `TRADEDATE`, `CLOSE` — в % от номинала, `ACCINT`). **Пагинация обязательна** — history-эндпоинт
   ISS отдаёт страницы по 100 строк с блоком `history.cursor`; используй тот же паттерн дочитывания,
   что в `GetBondizationAsync` (см. как это сделано после задачи T-1 этапа 12). Тест с фейковым
   `HttpMessageHandler` на склейку страниц — по образцу
   `tests/Bonds.Tests/Connectors/MoexBondizationPagingTests.cs`.
2. Дни без сделок (нет строки/`CLOSE=null`) — переносить последнюю известную цену вперёд
   (forward fill) на стороне потребителя, не клиента.

### B. Сервис бэкфилла (Core — математика, Infrastructure — оркестрация)

1. Чистый сервис `Bonds.Core/Analytics/PortfolioHistoryRebuildService.cs`: вход — полный журнал
   `Operation` счёта, карта исторических цен `instrumentId → (date → dirtyPriceRub за бумагу)`,
   номиналы/количества; выход — ряд `(DateOnly, decimal MarketValueRub, decimal? Xirr)` с шагом
   **неделя** (плюс последняя точка = сегодня). Алгоритм по каждой дате D:
   - восстановить количество каждой бумаги на D прогоном журнала операций до D (Buy/Sell меняют
     количество; см. типы в [Operation.cs](../src/Bonds.Core/Models/Operation.cs));
   - стоимость = Σ количество × грязная цена на D (forward fill; если цены нет вообще — пропустить
     бумагу и пометить точку флагом `Approximate`);
   - XIRR на D = `PortfolioXirrService.Calculate(операции ≤ D, стоимость на D, D)`
     ([PortfolioXirrService.cs](../src/Bonds.Core/Analytics/PortfolioXirrService.cs)).
2. Юнит-тесты: одна бумага, две покупки и купон → значения стоимости на контрольных датах руками;
   восстановление количества при продаже; отсутствие цен → `Approximate`.
3. Оркестратор в Infrastructure (`Bonds.Infrastructure/Analytics/PortfolioHistoryBackfillService.cs`):
   собирает входы (операции, ISIN→secid, история цен из MOEX), вызывает Core-сервис, пишет результат
   в `portfolio_value_snapshots` (upsert по дате — не задваивать с живыми снапшотами синка; живой
   снапшот за ту же дату **побеждает** бэкфилльный).
4. Запуск: (а) эндпоинт `POST /api/analytics/xirr/backfill` (single-user, идемпотентный, длительный —
   выполнять синхронно с разумным таймаутом, портфель маленький); (б) автоматически один раз при
   старте приложения, если снапшотов < 5, а операции есть (housekeeping-хук в существующем
   планировщике [SyncSchedulerHostedService.cs](../src/Bonds.Infrastructure/Scheduling/SyncSchedulerHostedService.cs)).

### C. Виджет (frontend)

[Analytics.tsx](../bonds-web/src/pages/Analytics.tsx), `XirrWidget` + `GET /api/analytics/xirr`
([AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs) — `history` уже несёт
`marketValueRub`):

1. Двухосевой график: линия XIRR (левая ось, %) + полупрозрачная area стоимости портфеля
   (правая ось, ₽, компактный формат «1,2 млн»). Recharts `ComposedChart`.
2. Тултип: дата, XIRR, стоимость. Подпись под графиком: «XIRR — внутренняя норма доходности по
   фактическим операциям счёта + текущая стоимость. История до {дата первого живого снапшота}
   восстановлена по дневным ценам MOEX (приближение)».
3. Пустое состояние оставить, но с кнопкой «Восстановить историю» → POST backfill → перезагрузка.
4. Vitest: рендер с историей, пустое состояние с кнопкой.

## Критерии приёмки

- [ ] MOEX history-клиент дочитывает все страницы (тест на пагинацию).
- [ ] Core-сервис восстанавливает стоимость и XIRR по контрольным точкам (юнит-тесты с ручными числами).
- [ ] Бэкфилл идемпотентен, живые снапшоты не перетираются.
- [ ] График показывает XIRR + стоимость с первой операции; из пустого состояния запускается бэкфилл.
- [ ] `dotnet test`, `yarn typecheck`/`test:run`/`build` зелёные; `pre-push-check` пройден.
