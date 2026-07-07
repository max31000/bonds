# Задача 26 — Банк облигаций: рыночная вселенная MOEX + гигиена + ликвидность (A1–A3)

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Конвенции — [CLAUDE.md](../CLAUDE.md):
> доходности на бэкенде — ДОЛИ, рубли — decimal, дюрация — годы. Перед пушем — `pre-push-check`
> (пуш делает оркестратор). Коммиты с префиксом T-26.
>
> **Зависимости:** нет. Задачи 27–30 строятся ПОВЕРХ этой — контракты API из части D обязательны.
>
> **Архитектурная рамка (двухъярусная, не нарушать):** банк — это ДЕШЁВЫЙ снимок всего рынка со
> статистикой, которую MOEX отдаёт готовой (YIELD/DURATION/обороты). Наш точный движок
> (BondMetricsCalculator) для банка НЕ запускается — он включается по требованию для избранных
> бумаг (это задача 27). Никаких массовых bondization-загрузок по 3000 бумаг.

---

## Проблема

Рекомендации сравнивают портфель только с его же бумагами + ручным watchlist. Нужен «банк» всех
облигаций MOEX с доходностью/дюрацией/ликвидностью — фундамент для выпадашки-сравнивалки (27),
скринера (28), конструктора (29) и relative value (30). Плюс дневная история снимков — B3 (30)
будет считать медианы корзин по нескольким дням.

## Что сделать

### A. Загрузка вселенной с MOEX ISS (Infrastructure)

1. Расширить `IMoexIssClient`/[MoexIssClient.cs](../src/Bonds.Infrastructure/Connectors/Moex/MoexIssClient.cs):
   метод `GetBondMarketSnapshotAsync(ct)` — весь рынок облигаций одним-двумя запросами:
   `/iss/engines/stock/markets/bonds/securities.json?iss.only=securities,marketdata&iss.meta=off`.
   **Пагинация обязательна** (тот же паттерн дочитывания, что в GetBondizationAsync/GetHistoryPricesAsync).
   Колонки проверь ФАКТИЧЕСКИ (структура ISS-ответа — истина): из `securities` — SECID, ISIN,
   SHORTNAME, SECNAME, FACEVALUE, LOTVALUE, COUPONVALUE/COUPONPERCENT, COUPONPERIOD, MATDATE,
   OFFERDATE, LISTLEVEL, SECTYPE/SECSUBTYPE, FACEUNIT (валюта!), STATUS; из `marketdata` — YIELD,
   DURATION (**в днях у MOEX — конвертируй в годы, задокументируй**), LAST/MARKETPRICE (в % от
   номинала), VALTODAY (оборот, руб), BID, OFFER, NUMTRADES. Каких-то колонок может не быть на
   отдельных режимах — парсер устойчив к null.
2. Дедупликация: одна бумага торгуется на нескольких режимах (board) — оставляй строку основного
   режима (или с максимальным VALTODAY). Только рублёвые (FACEUNIT=SUR/RUB), только не погашенные.
3. Тесты парсера — фейковый HttpMessageHandler с реалистичным JSON-фрагментом (2 страницы,
   дубль-board, бумага без marketdata), по образцу `MoexBondizationPagingTests`.

### B. Хранение: снимок + дневная история

Миграция (следующий номер, `ls src/Bonds.Infrastructure/Migrations/`, БЕЗ `;` в комментариях):
1. `bond_universe` — текущий снимок, upsert по `secid`: isin, short_name, sec_name, face_value,
   lot_value, coupon_percent, maturity_date, offer_date, list_level, sector (маппинг из
   SECTYPE/эмитента — переиспользуй [MoexSegmentMapper.cs](../src/Bonds.Infrastructure/Connectors/Moex/MoexSegmentMapper.cs)
   если применимо, иначе простая классификация гос/муни/корп), yield_fraction (**ДОЛЯ**: MOEX
   YIELD в процентах — раздели на 100, задокументируй), duration_years, price_percent,
   turnover_rub, bid_percent, offer_percent, num_trades, gspread_approx_fraction (см. C.3),
   is_floater-эвристика если распознаваема (иначе null), updated_at.
2. `bond_universe_history` — дневной срез для B3/трендов: (snapshot_date, secid, yield_fraction,
   duration_years, gspread_approx_fraction, turnover_rub, price_percent), PK (snapshot_date, secid),
   retention ~400 дней (чистка при записи).
3. Модель + `IBondUniverseRepository` (upsert батчем — не 3000 отдельных INSERT, собери multi-row
   команды или используй транзакцию с prepared statement; убедись что полный refresh < ~10 сек).

### C. Обновление + производные метрики (hosted service)

1. `BondUniverseRefreshService` (BackgroundService, по образцу
   [LiveQuotesPollingService.cs](../src/Bonds.Infrastructure/Quotes/LiveQuotesPollingService.cs)):
   в торговые часы — раз в час обновляет снимок; после закрытия (первый тик после 19:00 МСК) —
   пишет строку в `bond_universe_history` за сегодня (одна запись в день, идемпотентно).
   Options-класс с интервалами. Ошибки — Warning и пропуск итерации. Первый запуск после старта —
   сразу (не ждать часа), если снимок пуст или старше 6 часов.
2. **Скор ликвидности** (Core, чистая функция `LiquidityScoreCalculator`): вход turnover_rub,
   bid/offer (спред в % от цены), num_trades → скор 0–3 (None/Low/Medium/High) + оценка
   проскальзывания: `slippageEstimate = спред/2` (доля от суммы; при отсутствии bid/offer — null).
   Пороги в константах с doc-comment (например High: оборот > 5 млн ₽ и спред < 0.3%). Юнит-тесты.
3. **Приближённый G-спред**: `gspread_approx = yield_fraction − gcurve(duration_years)` по
   последнему сохранённому снимку кривой ([YieldCurveRepository](../src/Bonds.Infrastructure/Repositories/YieldCurveRepository.cs)
   — посмотри, как GSpreadCalculator интерполирует кривую, переиспользуй интерполяцию). Считается
   при refresh, хранится. Doc-comment: «приближение по данным MOEX, не наш точный движок».
4. **Гигиенический фильтр** (Core, чистый `UniverseHygieneFilter` + options): бумага «скрыта», если
   оборот < MinTurnoverRub (дефолт 100 тыс ₽/день) ИЛИ list_level=3 (опция, дефолт скрывать) ИЛИ
   yield_fraction > MaxSaneYield (дефолт 0.45 — рынок закладывает дефолт) ИЛИ дюрация/цена
   отсутствуют ИЛИ погашение < 14 дней. Возвращает причину скрытия (enum) — UI покажет
   «скрыто N: неликвид». Юнит-тесты на каждую причину.

### D. API (контракты для задач 27–30)

`Bonds.Api/Endpoints/UniverseEndpoints.cs` (авторизация как у всех):
1. `GET /api/universe?search=&maxDurationYears=&minDurationYears=&minYield=&maxYield=&sector=&includeHidden=false&sortBy=yield|duration|turnover|gspread&sortDir=&limit=50&offset=0`
   → `{ rows: [...], total, hiddenCount }`. Строка: secid, isin, name, sector, yieldFraction,
   durationYears, pricePercent, turnoverRub, listLevel, liquidityScore, slippageEstimateFraction,
   gspreadApproxFraction, maturityDate, offerDate, флаги (hidden+hiddenReason, inPortfolio,
   inWatchlist — join по ISIN с instruments/positions/watchlist). `search` — по SHORTNAME/SECNAME/
   ISIN/эмитенту, регистронезависимо.
2. `GET /api/universe/status` → { lastRefreshUtc, totalBonds, hiddenBonds, historyDays }.
3. Интеграционные тесты: сеанс с засеянной вселенной — поиск, сортировка, фильтры, includeHidden,
   флаги inPortfolio/inWatchlist, 401.

### E. Не делать (границы)

- НЕ прогонять банк через BondMetricsCalculator (только избранные — задача 27).
- НЕ строить UI (задача 28). НЕ трогать рекомендации (27/29).
- Рейтинги — вне скоупа.

## Критерии приёмки

- [ ] Снимок всего рынка загружается с пагинацией, дедупом board'ов, только RUB; refresh идёт по
      расписанию + при старте; дневная история пишется идемпотентно с retention.
- [ ] YIELD→доля, DURATION→годы задокументированы; gspread_approx и liquidityScore посчитаны.
- [ ] Гигиенический фильтр с причинами; `GET /api/universe` с поиском/фильтрами/сортировкой/флагами.
- [ ] `dotnet build/test` (юнит+интеграционные), `yarn typecheck/test:run/build/lint` — зелёные
      (фронт не трогаешь, но прогони — вдруг типы задел).
