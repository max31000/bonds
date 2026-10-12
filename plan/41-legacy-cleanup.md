# Задача 41 — Уборка легаси и дублей

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Контекст — этот файл +
> [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md).
>
> **Зависимости:** задачи 39–40 (сигналы уже переделаны, их правила не трогать).
> **Порядок в волне:** последняя.
>
> **Рамки:** ПОВЕДЕНИЕ НЕ МЕНЯЕТСЯ (кроме удаления мёртвого). Любой «живой» эндпоинт сохраняет
> маршрут и контракт. Перед удалением — доказать отсутствие вызовов (grep фронта `api/*.ts` и
> бэка). Не трогать UI/навигацию.

---

## Проблема

За 38 этапов накопились мёртвые эндпоинты/интерфейсы, четыре копии сборки «стоимости
портфеля», файл `AnalyticsEndpoints.cs` на 2446 строк и сотни комментариев «этап NN/plan/NN»,
часть которых уже вводит в заблуждение.

## Что сделать

1. **Удалить мёртвое** (сначала перепроверь grep'ом):
   - `GET /api/analytics/replacement-matrix` (AnalyticsEndpoints ~:34, ~:469–607, его DTO),
     `ReplacementMatrixService.BuildMatrix` и DTO, `tests/Bonds.IntegrationTests/ReplacementMatrixEndpointTests.cs`.
     `IsComparable` сохранить — вынести в маленький `YieldComparability` (Core/Analytics) и
     перевести вызовы (~:507, ~:1403 и др.).
   - Интерфейсы без реализаций: `IRatingProvider`, `ICandidateScreener`, `ITaxModel`.
   - `ITargetAllocationRepository` + `target_allocations` — если после задачи 40 никто не читает
     (лимит концентрации теперь из UserSettings) — удалить репозиторий/модель; таблицу — миграцией
     `DROP TABLE IF EXISTS` (следующий номер; обновить `MigrationIdempotencyTests`, включая список
     ожидаемых таблиц).
   - `PositionRepository.DeleteAsync`, если всё ещё без вызовов.
2. **Один источник «стоимости портфеля».** `PortfolioSnapshotService` (:48–69) — своя логика
   цены/пропуска → перевести на `PortfolioHoldingsBuilder` (как задача 40 сделала для сигналов),
   правило из задачи 39 (погашенные) сохранить. Задокументировать в CODEBASE-GUIDE одно правило
   MarketValue (грязная цена × количество; fallback-цепочка цены).
3. **Порог спреда** — одна Core-константа вместо
   `CandidateRiskSignalService.SpreadDeviationThresholdFraction` и
   `AnalyticsEndpoints.FairVerdictThresholdFraction` (BACKLOG).
4. **Разрезать `AnalyticsEndpoints.cs`** по швам (маршруты не меняются): `XirrEndpoints`,
   `PortfolioAnalyticsEndpoints` (composition/scatter/rate-scenario/trajectory),
   `ReplacementEndpoints` (comparison/replacement/candidates/RV/reliability),
   `AllocationEndpoints` (allocation/basket); DTO — в `Endpoints/Dtos/Analytics*.cs`.
   Регистрация в `Program.cs`. Тесты интеграции должны пройти без изменений.
5. `PortfolioCompositionInput.cs` фактически содержит `PortfolioHolding` — переименовать файл.
6. **Комментарии.** Пройтись по `src/` и `bonds-web/src`: убрать теги «этап NN / plan/NN /
   задача NN / T-NN», оставив суть-обоснование; исправить фактически неверные (например,
   `SyncCycleService.cs:27-28` «будущего POST /api/sync», количество правил в SignalsEngine).
   Не переписывать смысл комментариев о единицах измерения.
7. Обновить `docs/CODEBASE-GUIDE.md` (новые файлы эндпоинтов, YieldComparability, правило
   MarketValue, события вместо сигналов) и `README.md` (раздел «Что умеет»).

## Критерии приёмки

- [ ] Нет мёртвых эндпоинтов/интерфейсов из списка; grep подтверждает отсутствие ссылок.
- [ ] `AnalyticsEndpoints.cs` разрезан, все маршруты и контракты прежние (интеграционные тесты зелёные без правок ассертов).
- [ ] Снапшот стоимости строится через `PortfolioHoldingsBuilder`.
- [ ] Гайд и README актуальны.
- [ ] `scripts/pre-push-check.sh --all` — зелёный.
