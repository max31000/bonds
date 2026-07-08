# Задача 33 — Рыночные кандидаты-замены + риск-сигналы (бэкенд, блок 1)

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Контекст — этот файл +
> [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md) (**контракт
> единиц** — доходности/спреды в бэкенде ДОЛИ). Двухъярусные данные: банк (`bond_universe`) —
> дешёвая статистика MOEX для ранжирования; точный движок — по требованию через materialize.
>
> **Зависимости:** задача 31 (флоатеры исключены из RV/сравнений; `IsFloater` в банке). **После**
> волны A. Первая задача волны B (P1-2).
> **Порядок в волне:** первая; задача 34 может идти после (обе бэкенд, но делать
> **последовательно** — общий `RelativeValueService`/риск-сигналы и `AnalyticsEndpoints.cs`).
>
> **Рамки (что НЕ делать):**
> - НЕ трогать фронтенд (волна B фронт — задача 35).
> - НЕ считать «надёжность рейтинговых агентств» и не выдавать сигналы за неё. Владелец явно:
>   ранжируем по доходности, показываем **информационные риск-сигналы** (ликвидность+листинг и
>   спред), в будущем добавится отдельный тег рейтинга. Формулировки — «сигнал/оценка», не «надёжно».
> - НЕ материализовать всю вселенную (~3400 бумаг) через движок. Кандидаты ранжируются по банку;
>   точная выгода конкретной выбранной бумаги считается существующим `POST /api/analytics/replacement`.
> - НЕ вводить новые библиотеки. НЕ заводить issuer-поле в банке (его там нет — см. задачу 34).

---

## Проблема (в рамках переработки «Рекомендаций», ТЗ владельца)

Блок 1 новой страницы — «Слабые позиции → подобрать замену»: у каждой слабой позиции выпадашка с
режимами подбора кандидатов. Сейчас подбор размазан по трём разным местам (слабые звенья, матрица
замен, RV-секции) и по разным путям данных. Нужен **единый бэкенд-источник кандидатов-замен для
конкретной позиции**, отдающий:
- режим **«доходные рынка»** — вся фикс-купонная гигиенически-чистая вселенная, отсортированная по
  доходности убыв., БЕЗ флоатеров, с риск-сигналами на каждом кандидате;
- режим **«дешёвые соседи по корзине (RV)»** — существующая RV-логика дешёвых соседей корзины
  сектор×дюрация позиции (переиспользовать `RelativeValueService`/`BuildCheapCandidates`);
- (режим «поиск» реализуется фронтом через существующий `GET /api/universe` + `POST
  /api/analytics/replacement`; отдельного бэкенда не требует — но риск-сигналы должны быть
  доступны и там, см. A2).

Риск-сигналы — **информационные**, не ранжируют. Два сигнала на кандидата:
- **Ликвидность+листинг** — переиспользовать существующий `LiquidityScoreCalculator`
  ([src/Bonds.Core/Universe/LiquidityScoreCalculator.cs](../src/Bonds.Core/Universe/LiquidityScoreCalculator.cs),
  enum `LiquidityScore { None, Low, Medium, High }`) + `BondUniverseEntry.ListLevel`.
- **Спред** — отклонение G-спреда бумаги (`BondUniverseEntry.GspreadApproxFraction`) от медианы её
  корзины сектор×дюрация (переиспользовать инфраструктуру корзин `RelativeValueService` /
  `RelativeValueSnapshotBuilder`).

## Что сделать

### A. Сервис риск-сигналов (переиспользуемый, `Bonds.Core`)

1. Новый чистый сервис `src/Bonds.Core/Analytics/CandidateRiskSignalService.cs` (или расширить
   существующий, если найдётся уместный — сначала поищи). Вход: `BondUniverseEntry` (или его поля)
   + медиана G-спреда корзины (из RV-снимка). Выход — record:
   ```
   public enum SignalLevel { Good, Neutral, Caution }   // Good=позитивный сигнал, Caution=негативный
   public sealed record CandidateRiskSignals(
       SignalLevel Liquidity,           // из LiquidityScore + ListLevel
       string LiquidityLabel,           // напр. "Высокая ликвидность, листинг 1" / "Низкий оборот, листинг 3"
       SignalLevel Spread,              // отклонение G-спреда от медианы корзины
       decimal? GSpreadFraction,        // ДОЛЯ; из GspreadApproxFraction
       decimal? SpreadVsBasketMedianFraction); // ДОЛЯ, знак: >0 спред выше медианы (премия/риск)
   ```
   - **Ликвидность→уровень:** `High`→Good; `Medium`→Neutral; `Low`/`None`→Caution. Дополнительно:
     `ListLevel == 3` тянет к Caution (комбинируй: High+ListLevel3 → Neutral). Точную матрицу
     зафиксировать в doc-comment и тестах.
   - **Спред→уровень:** `SpreadVsBasketMedianFraction = gspread − basketMedian`. Порог возьми
     согласованным с RV (`FairVerdictThresholdFraction = 0.0020` = 20 б.п., см.
     [AnalyticsEndpoints.cs:1090](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)). Спред заметно
     **выше** медианы → `Caution` (рынок закладывает повышенный риск/премию); в пределах порога →
     `Neutral`; заметно **ниже** → `Good` (спокойнее, но и доходность ниже). Метки-подписи —
     нейтральные, описательные, без слова «надёжно».
   - `GspreadApproxFraction == null` → `Spread = Neutral`, значения `null`. `null`-ликвидность-данные
     → `Neutral`, не `Caution`.
2. Единицы: все спреды — ДОЛИ (контракт). Doc-comment на каждом поле «из чего».

### B. Эндпоинт кандидатов-замен для позиции (блок 1)

1. Новый `GET /api/analytics/replacement-candidates?positionId={id}&mode={market|rv}&limit={n}`
   в [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs) (статик-метод +
   регистрация; авторизация — глобальная FallbackPolicy). Резолв позиции — по образцу соседних
   эндпоинтов (`BuildForAccountAsync` → найти holding по `positionId`).
2. **mode=market:** взять гигиенически-чистую (`UniverseHygieneFilter`) фикс-купонную вселенную
   банка (исключить `IsFloater == true` — уже стрипается в RV/можно фильтром), исключить сам ISIN
   позиции; отсортировать по `YieldFraction` убыв.; взять топ-`limit` (дефолт напр. 20). Для каждого
   — посчитать `CandidateRiskSignals` (медиана корзины из RV-снимка). Вернуть дешёвую банк-статистику
   (secid, isin, name, issuer=null на банк-слое, sector, yieldFraction, durationYears,
   gspreadFraction, riskSignals). Точную выгоду тут НЕ считать (её посчитает `POST /replacement` при
   выборе).
3. **mode=rv:** переиспользовать существующую логику дешёвых соседей корзины позиции
   (`RelativeValueService` + `BuildCheapCandidates`,
   [AnalyticsEndpoints.cs:1184](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) — вынести/вызвать
   так, чтобы вернуть тот же DTO кандидата с `riskSignals`. Флоатеры уже исключены (задача 31).
   Если у позиции нет валидной корзины/спреда — вернуть пустой список + понятный признак (не 500).
3. DTO ответа — `sealed record` с `required`, дисклеймер-строка в ответе («кандидаты/оценки, не
   инвестрекомендация; риск-сигналы — по биржевой статистике, не рейтинг агентств»). Полную схему
   DTO задать в этом файле, т.к. её зеркалит задача 35:
   ```
   ReplacementCandidatesResponseDto {
     string Mode; string PositionIsin;
     IReadOnlyList<ReplacementCandidateDto> Candidates;
     string Disclaimer;
   }
   ReplacementCandidateDto {
     string Secid; string? Isin; string Name; string? Issuer; string? Sector;
     decimal? YieldFraction; decimal? DurationYears; decimal? GSpreadFraction;
     RiskSignalsDto RiskSignals;
   }
   RiskSignalsDto {
     string Liquidity;      // "Good"|"Neutral"|"Caution"
     string LiquidityLabel;
     string Spread;         // "Good"|"Neutral"|"Caution"
     decimal? GSpreadFraction;
     decimal? SpreadVsBasketMedianFraction;
   }
   ```
4. Риск-сигналы должны быть доступны и для пути «поиск» блока 1. Прагматично: добавить
   `RiskSignalsDto` в ответ существующего `POST /api/analytics/replacement`
   ([AnalyticsEndpoints.cs:320](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) для выбранной
   цели (сигналы считаются по её банк-записи). Если это раздувает задачу — вынести в FOLLOWUP и
   согласовать с оркестратором (эскалация), но предпочтительно сделать здесь.

### C. Тесты (эталонные числа руками)

Юнит:
1. `CandidateRiskSignalService`: матрица (LiquidityScore×ListLevel)→SignalLevel по всем веткам;
   спред выше/на уровне/ниже медианы (эталонные доли, порог 0.0020) → Caution/Neutral/Good;
   `null`-спред→Neutral.
2. mode=market: сортировка по доходности убыв., флоатеры отсутствуют, сам ISIN исключён.
3. mode=rv: возвращает дешёвых соседей корзины позиции (переиспользование RV), флоатеров нет.

Интеграционные:
4. `GET /api/analytics/replacement-candidates` mode=market и mode=rv на посеянных данных: 200,
   корректный состав, риск-сигналы присутствуют; несуществующий positionId → 404/422 по образцу.

## Критерии приёмки

- [ ] Есть единый эндпоинт кандидатов-замен для позиции с режимами market/rv; флоатеров нет ни в
      одном режиме.
- [ ] Каждый кандидат несёт два риск-сигнала (ликвидность+листинг, спред) с уровнями Good/Neutral/
      Caution и подписями; формулировки не выдают их за рейтинг агентств.
- [ ] Ранжирование market — по доходности убыв. (сигналы не ранжируют).
- [ ] Единицы: все спреды — доли, задокументированы; `null`-данные → Neutral, не Caution.
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
