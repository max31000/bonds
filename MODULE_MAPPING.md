# Маппинг «модуль спеки → код» (spec §10, §12.1)

> Обновляется по факту реализации. Источник модулей — `bond-portfolio-analytics-spec.md` §10. Плановое соответствие «модуль → этап» — `plan/00-overview-and-architecture.md` §4; здесь — фактическое соответствие «модуль → код» после реализации.

| # | Модуль (спека §10) | Этап | Где в коде | Ключевые классы |
|---|---|---|---|---|
| 1 | Broker Sync (T-Invest) | 04 | `src/Bonds.Infrastructure/Connectors/TInvest/` | `ITInvestPortfolioClient`, `TInvestPortfolioClient`, `TInvestOperationMapper`, `TInvestNumeric` |
| 2 | Reference Data (MOEX ISS) | 04 | `src/Bonds.Infrastructure/Connectors/Moex/` | `IMoexIssClient`, `MoexIssClient`, `MoexBondizationParser`, `MoexSecuritiesParser`, `MoexGcurveParser`, `IssTable` |
| 3 | Storage | 03 | `src/Bonds.Core/Models/`, `src/Bonds.Core/Interfaces/Repositories/`, `src/Bonds.Infrastructure/Repositories/`, `src/Bonds.Infrastructure/Migrations/*.sql` | По одной модели/репозиторию на агрегат §5; `MigrationRunner`, `DapperTypeHandlers` |
| 4 | Calculation Engine | 05 | `src/Bonds.Core/Calculation/` | `AccruedInterestCalculator`, `YtmCalculator`, `YieldToOfferCalculator`, `DurationCalculator`, `GSpreadCalculator`, `XirrCalculator`, `BondMetricsCalculator`, `OfferCutoffResolver`, `IrrSolver` — чистый модуль, без I/O |
| 5 | Cash-Flow Projection | 06 | `src/Bonds.Core/CashFlow/`, `src/Bonds.Infrastructure/CashFlow/` | `CashFlowProjectionService`, `CashFlowAggregator` (чистые); `CashFlowProjectionOrchestrator` (I/O-обёртка) |
| 6 | Portfolio Analytics | 06 | `src/Bonds.Core/Analytics/`, `src/Bonds.Infrastructure/Analytics/` | `PortfolioXirrService`, `PortfolioCompositionService`, `PositionComparisonService`, `SwitchAnalysisService` (чистые); `PortfolioSnapshotService`, `PortfolioHoldingsBuilder` (I/O) |
| 7 | Signals Engine | 07 | `src/Bonds.Core/Signals/` | Правила-триггеры §8, `SignalEngineOptions`, `SignalDeduplicator` — чистый модуль |
| 8 | Scheduler | 07 | `src/Bonds.Infrastructure/Scheduling/` | `SyncCycleService` (singleton, оркестрация цикла), `SyncSchedulerHostedService` (`BackgroundService`, окна MSK), `ISyncCycleRunner` |
| 9 | Backend API | 08 | `src/Bonds.Api/Endpoints/`, `src/Bonds.Api/Middleware/` | `PositionsEndpoints`, `CashFlowEndpoints`, `AnalyticsEndpoints`, `SignalsEndpoints`, `SyncEndpoints`, `SettingsEndpoints`, `AuthEndpoints` (этап 02); `ErrorHandlingMiddleware` |
| 10 | Frontend | 09a (готово) / 09b–09c (в работе) | `bonds-web/src/` | `pages/Positions.tsx` (09a); `pages/CashFlow.tsx`, `pages/Analytics.tsx`, `pages/Signals.tsx`, `pages/Settings.tsx` (09b/09c — состав уточнится по факту коммитов) |

## Сквозные модули (не входят в нумерацию §10, но пронизывают весь стек)

| Модуль | Где в коде | Заметка |
|---|---|---|
| Авторизация (Telegram + JWT) | `src/Bonds.Core/Services/ITelegramAuthService.cs`, `Infrastructure/Services/TelegramAuthService.cs`, `Api/Endpoints/AuthEndpoints.cs` | Этап 02, сквозной слой — не модуль §10, но защищает все остальные |
| Точки расширения (§11 этапа 00, не реализованы) | `Bonds.Core/Analytics/ICandidateScreener.cs`, `ITaxModel.cs`, `IRatingProvider.cs` | Интерфейсы без DI-регистрации и реализации — заглушки под пост-MVP фичи (скринер по вселенной, налоговое моделирование, кредитные рейтинги) |
| Мультисчёт (точка расширения) | `AccountId`/`UserId` в `Position`/`Operation`/моделях | Поле есть везде, логика работает с одним счётом (single-user MVP) |

## Известные расхождения с исходным планом (см. `BACKEND_DECISIONS.md`/`DEVOPS_DECISIONS.md` для деталей)

- G-спред (модуль 4) — реализован по официальной методике MOEX (гауссовы поправки), не по упрощённой интерполяции, как изначально предполагал план 05 — см. Решение 2 в `BACKEND_DECISIONS.md`.
- Стакан (bid/ask, относится к модулю 1/9) — запрашивается, но не персистируется в БД — осознанно вне MVP (спека §8 относит к «на будущее»).
- T-Invest токен через UI (модуль 9) — хранится отдельно от ENV-токена синка, приоритет БД→ENV, но сам коннектор (модуль 1) пока не переключён на чтение из этого хранилища — читает только ENV.
