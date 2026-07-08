# Задача 32 — Флоатеры видны, но помечены: скринер + сравнивалка (фронтенд)

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Контекст — этот файл +
> [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md). Фронт: React 19
> + TS strict + Mantine 9 + Zustand; менеджер — **yarn** (`cd bonds-web && yarn <script>`).
> Фронт-типы **буквально зеркалят** бэкенд-DTO, включая строковые enum-значения.
>
> **Зависимости:** задача 31 (проброс `isFloater` в `GET /api/universe` + 422 на цель-флоатер в
> `POST /api/analytics/replacement`). **Строго после** 31.
> **Порядок в волне:** вторая (последняя) в волне A. Закрывает P1-1.
>
> **Рамки (что НЕ делать):**
> - НЕ переделывать страницу «Рекомендации» (это волна B, задачи 33–35). Здесь только скринер и
>   общий компонент сравнивалки `MarketComparator`.
> - НЕ прятать флоатеры из скринера — владелец выбрал «пометка + фильтр», фильтр «только
>   фикс-купон» по умолчанию **ВЫКЛ**.
> - НЕ вводить новые библиотеки.

---

## Проблема

Владелец должен видеть флоатеры в скринере, но не иметь возможности «случайно купить флоатер»
через доходность/сравнение. Сейчас на фронте:

1. **Скринер показывает YIELD флоатера как YTM.** Колонка «YTM»
   ([Screener.tsx:451,503](../bonds-web/src/pages/Screener.tsx)) рендерит `formatPercent(row.yieldFraction)`
   для всех бумаг; `UniverseRow` не несёт `isFloater`
   ([types.ts:815-834](../bonds-web/src/api/types.ts)); фильтра «тип купона» нет в `FilterPanel`
   ([Screener.tsx:121-213](../bonds-web/src/pages/Screener.tsx)). Дефолтная сортировка — по yield
   убыв. → флоатеры оказываются в топе «доходности».
2. **Выпадашка-цели сравнивалки включает флоатеры.** `MarketComparator`
   ([MarketComparator.tsx:91](../bonds-web/src/components/MarketComparator.tsx)) грузит топ-10 по
   yield из банка без фильтра типа купона → флоатер можно выбрать целью и получить (после задачи 31)
   422 либо, без неё, бессмысленную карточку выгоды.
3. **Баг-контракт `yieldKind` (подтверждён по коду).** Бэкенд сериализует
   `YieldKind.CurrentYield.ToString()` → строку **`"CurrentYield"`**
   ([AnalyticsEndpoints.cs:289](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs);
   enum [PositionComparisonService.cs:78-82](../src/Bonds.Core/Analytics/PositionComparisonService.cs)),
   а фронт-тип — `type YieldKind = 'Ytm' | 'Current'`
   ([types.ts:271](../bonds-web/src/api/types.ts)), и стор проверяет `row.yieldKind === 'Current'`
   ([useRecommendationsStore.ts:23](../bonds-web/src/store/useRecommendationsStore.ts)). Значение
   `'Current'` **никогда не равно** `"CurrentYield"` → ветка «вне сравнения» по yieldKind не
   срабатывает в проде, и флоатер с ненулевым CurrentYield и `dataIncomplete=false` **протекает** в
   ранжирование sell-кандидатов. Тест зелёный только потому, что мок использует `'Current'`
   ([Recommendations.test.tsx:82](../bonds-web/src/pages/Recommendations.test.tsx)).

## Что сделать

### A. Fix баг-контракта `yieldKind` (сначала — он самостоятелен и критичен)

1. В [types.ts:271](../bonds-web/src/api/types.ts) заменить `type YieldKind = 'Ytm' | 'Current'`
   на `'Ytm' | 'CurrentYield'` (зеркалит бэкенд-сериализацию enum).
2. В [useRecommendationsStore.ts:23](../bonds-web/src/store/useRecommendationsStore.ts) —
   `row.yieldKind === 'CurrentYield'`.
3. Обновить все моки/фикстуры, где `yieldKind: 'Current'` → `'CurrentYield'`
   (`Recommendations.test.tsx`, `test/msw-handlers.ts`, прочие — найти grep-ом по `'Current'`).
   Добавить регресс-тест: строка с `yieldKind: 'CurrentYield'` и `dataIncomplete: false` попадает в
   «вне сравнения», а не в sell-кандидаты.

### B. Скринер — пометка флоатера + фильтр «только фикс-купон»

1. Добавить `isFloater?: boolean | null` в `UniverseRow`
   ([types.ts:815-834](../bonds-web/src/api/types.ts)) — зеркало DTO из задачи 31.
2. Колонка «YTM» ([Screener.tsx:503](../bonds-web/src/pages/Screener.tsx)): если
   `row.isFloater === true` — показывать не число, а бейдж/текст «плавающий» (напр. Mantine `Badge`
   «плав. купон» + прочерк вместо доходности; при желании — `Tooltip` «YIELD флоатера —
   текущая доходность, несравнима с YTM»). Значение `yieldFraction` флоатера **не** показывать как
   доходность.
3. В `FilterPanel` ([Screener.tsx:121-213](../bonds-web/src/pages/Screener.tsx)) добавить чекбокс/
   свитч **«Только фикс-купон»** (по умолчанию ВЫКЛ). Хранить в `ScreenerFilters`
   ([useScreenerStore.ts:11-19](../bonds-web/src/store/useScreenerStore.ts)). Фильтрация
   клиентская (как остальные in-memory-фильтры скринера) — прятать строки с `isFloater === true`
   при включённом фильтре. `isFloater == null`/`undefined` — считать «не флоатер» (не прятать).
4. Дефолтную сортировку по yield НЕ менять (владелец не просил), но пометка + прочерк доходности у
   флоатеров устраняет иллюзию «топ доходности».

### C. Сравнивалка — исключить флоатеры-цели

1. В `MarketComparator` ([MarketComparator.tsx:91](../bonds-web/src/components/MarketComparator.tsx))
   отфильтровать опции: не показывать `row.isFloater === true` в выпадашке целей (клиентский фильтр
   после `fetchUniverse`; при желании подтянуть чуть больше строк, чтобы после отсева осталось ~10).
2. Обработать 422 от `POST /api/analytics/replacement` (задача 31): если сервер вернул
   `type: 'ValidationException'` — показать понятную ошибку («несравнимо — выберите фикс-купонную
   бумагу»), не роняя компонент. Проверить, как соседние вызовы `postReplacement` обрабатывают
   ошибки, и переиспользовать паттерн.

### D. Vitest-кейсы

1. Скринер: строка-флоатер показывает бейдж «плав. купон» и не показывает число в колонке YTM.
2. Скринер: фильтр «только фикс-купон» скрывает флоатеры; по умолчанию они видны.
3. `MarketComparator`: флоатер отсутствует в опциях выпадашки; выбор недоступен.
4. Регресс `yieldKind`: `'CurrentYield'` → строка уходит в «вне сравнения» (из B1).

## Критерии приёмки

- [ ] В скринере флоатер помечен, его YIELD не выдаётся за YTM; есть фильтр «только фикс-купон»
      (дефолт ВЫКЛ), скрывающий флоатеры.
- [ ] В выпадашке сравнивалки нет флоатеров; прямой 422 обрабатывается сообщением, а не крешем.
- [ ] `yieldKind` фронт-тип/логика/моки зеркалят `"CurrentYield"`; регресс-тест ловит протечку
      флоатера в sell-кандидаты.
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
