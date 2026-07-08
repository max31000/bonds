# Задача 31 — Флоатеры не «протекают» в сравнения, RV и аллокацию (бэкенд)

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (паттерны и **контракт единиц** — читать обязательно; доходности/спреды везде в бэкенде —
> ДОЛИ).
>
> **Зависимости:** нет. Это первая задача волны A (P1-1). Фронтовую поверхность (скринер-бейдж,
> фильтр, исключение целей в сравнивалке, fix `yieldKind`-литерала) делает задача 32 — **строго
> после** этой.
> **Порядок в волне:** первая; задача 32 после `--ff-only` мержа этой.
>
> **Рамки (что НЕ делать):**
> - НЕ трогать фронтенд (кроме, при необходимости, значений строковых enum в DTO — но фронт-типы
>   правит задача 32).
> - НЕ прятать флоатеры из выдачи `GET /api/universe` (владелец выбрал «пометка + фильтр», а не
>   hygiene-hide). Новую причину `HygieneHiddenReason` НЕ заводить.
> - НЕ блокировать `POST /api/universe/{secid}/materialize` для флоатеров глобально (materialize —
>   общий путь просмотра любой бумаги). Блокируем только путь сравнения выгоды (см. B4).
> - НЕ вводить новые библиотеки. НЕ менять формулы движка.

---

## Проблема

Флоатеры (плавающий купон) несравнимы по доходности с фикс-купоном: их биржевой `YIELD` —
это текущая доходность (CurrentYield), а не YTM. Единая точка правды сравнимости
`ReplacementMatrixService.IsComparable(isFloater, isIndexed, dataIncomplete)`
([src/Bonds.Core/Analytics/ReplacementMatrixService.cs:367](../src/Bonds.Core/Analytics/ReplacementMatrixService.cs))
уже исключает флоатеры-**позиции** портфеля из матрицы замен и RV-вердиктов. Но флоатеры-**цели**
(рыночные бумаги из банка `bond_universe`) не исключаются нигде на бэкенде:

1. **RV-корзины загрязнены.** `BasketMember` не несёт признак флоатера
   ([RelativeValueService.cs:37](../src/Bonds.Core/Analytics/RelativeValueService.cs)), поэтому
   приближённый G-спред флоатеров (посчитанный из вводящего в заблуждение биржевого YIELD) входит
   в **медиану корзины** сектор×дюрация → искажает Cheap/Fair/Rich-вердикты всех бумаг корзины.
2. **«Дешёвые соседи» RV предлагают флоатеры.** `BuildCheapCandidates`
   ([AnalyticsEndpoints.cs:1184](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) берёт кандидатов
   из `snapshot.AllMembers` без фильтра — флоатер может быть рекомендован как «дешёвый аналог»
   фикс-купонной позиции.
3. **Аллокация «куда вложить» смешивает флоатеры с фиксом** по `EffectiveYield = CurrentYield`:
   кандидаты `GetAllocation` ([AnalyticsEndpoints.cs:741-827](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs))
   не отсекают флоатеры, а `CashAllocationService.Allocate` сортирует по убыванию `EffectiveYield`
   ([CashAllocationService.cs:59](../src/Bonds.Core/Analytics/CashAllocationService.cs)) — флоатер
   с высоким CurrentYield всплывёт наверх как «самый доходный».
4. **Карточка выгоды сравнивает несравнимое.** `POST /api/analytics/replacement`
   ([AnalyticsEndpoints.cs:320](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) не проверяет
   `IsComparable` у цели: спред цели-флоатера = `CurrentYield(флоатер) − YtmEffective(фикс)` —
   бессмысленная величина, показанная пользователю как «выгода».
5. **Банк не отдаёт признак флоатера наружу.** `UniverseRowDto`
   ([UniverseEndpoints.cs:338-358](../src/Bonds.Api/Endpoints/UniverseEndpoints.cs)) не несёт
   `IsFloater`, хотя `BondUniverseEntry.IsFloater` (`bool?`, эвристика по BONDTYPE) уже посчитан
   маппером ([BondUniverseEntryMapper.cs:63](../src/Bonds.Infrastructure/Universe/BondUniverseEntryMapper.cs)).
   Без проброса фронт (задача 32) не сможет пометить/отфильтровать флоатеры в скринере и убрать их
   из выпадашки-целей сравнивалки.

Данных достаточно: `BondUniverseEntry` уже несёт `IsFloater` (`bool?`, `null` = BONDTYPE не пришёл),
`ListLevel`, `TurnoverRub`, `Sector`, `YieldFraction`, `GspreadApproxFraction`.

**Продуктовое решение владельца:** в скринере флоатеры **видны, но помечены** (это задача 32); в
сравнениях/RV/аллокации — **жёстко исключены** (эта задача). `IsFloater == null` (BONDTYPE не
пришёл) трактуем как «не флоатер» (не исключаем) — иначе потеряем бумаги с неполным справочником;
это осознанный дефолт, задокументировать в коде.

## Что сделать

### A. Проброс признака флоатера в банк-контур (для задачи 32)

1. Добавить `bool? IsFloater` в `UniverseRowDto`
   ([UniverseEndpoints.cs:338-358](../src/Bonds.Api/Endpoints/UniverseEndpoints.cs)), заполнять из
   `entry.Entry.IsFloater` в маппере строки (там же, где собираются прочие поля из
   `BondUniverseEntry`). Ничего не скрывать, сортировку/фильтры не менять — только добавить поле.
2. Проверить, что `GetUniverseStatus` не ломается (то же поле не требуется — не добавлять, если не
   нужно).

### B. Жёсткое исключение флоатеров из сравнений/RV/аллокации

Везде использовать существующий предикат сравнимости, а не заводить новый. Правило исключения:
**исключаем, если `IsFloater == true`** (для банк-слоя, где нет `IsIndexed`) либо
`!IsComparable(...)` (для слоя движка, где есть оба флага). `IsFloater == null` → НЕ исключаем.

1. **RV-корзины (медианы).** Добавить `bool IsFloater` в record `BasketMember`
   ([RelativeValueService.cs:37-43](../src/Bonds.Core/Analytics/RelativeValueService.cs)).
   В сборщике снимка `RelativeValueSnapshotBuilder`
   ([src/Bonds.Infrastructure/Universe/RelativeValueSnapshotBuilder.cs:133,155-164,204-213](../src/Bonds.Infrastructure/Universe/RelativeValueSnapshotBuilder.cs))
   — при формировании `BasketMember` проставлять `IsFloater = entry.IsFloater == true` и
   **исключать флоатеры из членов корзины** (не добавлять в список, по которому считается медиана).
   Убедиться, что это не роняет `MinBasketSize`-fallback (корзина→сектор→рынок) — просто корзины
   станут меньше на число флоатеров.
2. **«Дешёвые соседи» RV.** В `BuildCheapCandidates`
   ([AnalyticsEndpoints.cs:1184-1223](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) отфильтровать
   кандидатов-флоатеров (после того как п.1 уберёт их из `snapshot.AllMembers` — проверить, что
   `AllMembers` теперь без флоатеров; если `AllMembers` собирается отдельно от членов корзины —
   добавить фильтр и там).
3. **Аллокация.** В `GetAllocation`
   ([AnalyticsEndpoints.cs:741-827](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) исключить
   флоатеры из набора кандидатов: и из портфельных holdings (`holding.IsFloater` — модель
   `PortfolioHolding` его несёт, см. [PortfolioHoldingsBuilder.cs:151](../src/Bonds.Infrastructure/Analytics/PortfolioHoldingsBuilder.cs)),
   и из watchlist-ветки. Использовать `ReplacementMatrixService.IsComparable(h.IsFloater, h.IsIndexed,
   h.DataIncomplete)` как предикат допуска (это заодно отсечёт индексируемые — корректно). Записать
   отсечённые как `AllocationSkipDto` с причиной (если у Skip уже есть «reason» — добавить значение
   вроде `"Floater"`/`"Несравнимо"`; сверься с существующими причинами skip и не плоди дубли).
4. **Карточка выгоды (сравнивалка).** В `PostReplacement`
   ([AnalyticsEndpoints.cs:320-438](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs)) — если
   **цель** (materialized-бумага) `IsFloater || IsIndexed` (т.е. `!IsComparable`), вернуть **422**
   через `Results.Json(new { error, type = "ValidationException" }, statusCode: 422)` по образцу
   соседних валидаций, с сообщением в духе «Бумага с плавающим/индексируемым купоном несравнима по
   доходности с фикс-купоном — выберите фикс-купонную бумагу». Фронт (задача 32) исключит флоатеры
   из выпадашки заранее, но 422 — защита контракта на случай прямого вызова.

### C. Единицы

Новых величин с единицами измерения не вводится — только булев флаг `IsFloater` и фильтрация.
При добавлении `IsFloater` в `BasketMember` — doc-comment «эвристика по BONDTYPE, из
`BondUniverseEntry.IsFloater`; `null`→false (не исключаем)».

### D. Тесты (обязательны, эталонные числа руками)

Юнит (`tests/Bonds.Tests`, без сети/БД):
1. `RelativeValueService`/снимок: корзина из фикс-бумаг + один флоатер с аномальным G-спредом →
   медиана считается **без** флоатера (сравнить с эталоном, посчитанным руками по фикс-членам).
2. `BuildCheapCandidates`: среди кандидатов есть флоатер с «дешёвым» спредом → он **не** попадает
   в выдачу дешёвых соседей.
3. `CashAllocationService`/`GetAllocation`-хелпер: кандидат-флоатер с самым высоким EffectiveYield
   → исключён из аллокации, попал в skip; фикс-кандидаты распределены как эталон.
4. Предикат исключения: `IsFloater == null` → бумага НЕ исключается (краевой кейс).

Интеграционные (`tests/Bonds.IntegrationTests`, Testcontainers):
5. `POST /api/analytics/replacement` с целью-флоатером → **422** с `type=ValidationException`.
6. `GET /api/universe` возвращает `isFloater` в строках (поле присутствует, значение корректно для
   посеянного флоатера и фикса).

## Критерии приёмки

- [ ] Флоатер никогда не входит в медиану RV-корзины и не появляется в «дешёвых соседях».
- [ ] Флоатер не попадает в аллокацию «куда вложить» (отражён в skip с внятной причиной).
- [ ] `POST /api/analytics/replacement` с целью-флоатером/индексируемой → 422, а не «выгода».
- [ ] `GET /api/universe` отдаёт `isFloater` в каждой строке; выдача/сортировка/фильтры скринера
      по составу не изменились (флоатеры по-прежнему видны).
- [ ] `IsFloater == null` трактуется как «не флоатер» и задокументирован в коде.
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный (нужен Docker).
