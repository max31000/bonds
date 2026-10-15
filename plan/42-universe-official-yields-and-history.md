# Задача 42 — Биржевые метрики в банке облигаций + расширенная история

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (паттерны и контракт единиц — читать обязательно).
>
> **Зависимости:** нет (первая задача волны В1 цикла docs/2026-10-analytics-quality-upgrade.md).
> **Порядок в волне:** строго первая; задачи 43–44 стартуют после неё.
>
> **Рамки (что НЕ делать):** не менять матрицу светофора и вердикты RV (это задачи 44/46); не
> трогать точный движок расчёта (`Bonds.Core/Calculation/*`) — он остаётся источником метрик для
> позиций; не добавлять эмитента (задача 43).

---

## Проблема

Банк облигаций (`bond_universe`) строится из блоков `securities,marketdata` MOEX ISS, а блок
`marketdata_yields` не запрашивается вообще. В нём биржа сама отдаёт G-спред (GSPREADBP),
Z-спред, эффективную доходность и дюрацию, а также тип даты доходности (к оферте/к погашению).
Мы вместо этого считаем приближение `GspreadApprox` по последней сохранённой кривой
(`Universe/BondUniverseEntryMapper.cs`) — оно расходится с биржевым и смешивается в корзинах RV
с точными спредами позиций. Кроме того, в банке нет НКД (ACCRUEDINT) — из-за этого аллокация
«весь рынок» считает цену лота с НКД = 0 (`AllocationEndpoints.cs`, ~441–464), и нет ISSUESIZE.
История банка (`bond_universe_history`) хранит 5 полей в день — без bid/ask и листинга, поэтому
динамические сигналы (расширение спреда, деградация ликвидности) строить не на чем.
Живой пробой 2026-10-15 подтверждено: `GET /iss/engines/stock/markets/bonds/securities.json?iss.only=marketdata_yields`
отдаёт 2512 строк, GSPREADBP заполнен у 2437; колонки:
`SECID, BOARDID, PRICE, YIELDDATE, ZCYCMOMENT, YIELDDATETYPE, EFFECTIVEYIELD, DURATION,
ZSPREADBP, GSPREADBP, WAPRICE, EFFECTIVEYIELDWAPRICE, DURATIONWAPRICE, IR, ICPI, BEI, CBR,
YIELDTOOFFER, YIELDLASTCOUPON, TRADEMOMENT, SEQNUM, SYSTIME`.

## Что сделать

### A. MOEX: тянуть marketdata_yields и новые поля securities

1. `MoexIssClient` (запрос банка, ~строки 317–318): добавить `marketdata_yields` в `iss.only`,
   в `securities.columns`/`marketdata.columns` — ACCRUEDINT и ISSUESIZE (проверить фактические
   имена по живому ответу; ACCRUEDINT — в блоке securities). Распарсить блок в
   `MoexBondUniverseParser` (новые поля в `MoexBondMarketRow`). Дедупликация по SECID уже есть
   (board с максимальным оборотом) — для `marketdata_yields` джойнить по SECID+BOARDID выбранного
   борда, при отсутствии строки — поля null.
2. Новый реальный JSON-снимок в `tests/Bonds.Tests/Fixtures/Moex/` (взять живой ответ с
   `iss.only=securities,marketdata,marketdata_yields` и парой десятков бумаг, включая бумагу без
   строки в marketdata_yields).

### B. Схема и маппинг

1. Миграция `017_universe_official_yields.sql` (+ обновить `MigrationIdempotencyTests`: число и
   список):
   - `bond_universe` добавить: `accrued_rub DECIMAL(18,6) NULL`, `issue_size BIGINT NULL`,
     `effective_yield_fraction DECIMAL(12,8) NULL`, `yield_to_offer_fraction DECIMAL(12,8) NULL`,
     `yield_date_type VARCHAR(16) NULL`, `gspread_official_fraction DECIMAL(12,8) NULL`,
     `zspread_official_fraction DECIMAL(12,8) NULL`;
   - `bond_universe_history` добавить: `bid DECIMAL(12,6) NULL`, `offer DECIMAL(12,6) NULL`,
     `list_level TINYINT NULL`, `gspread_effective_fraction DECIMAL(12,8) NULL`.
2. Единицы (doc-comment на каждой конверсии, контракт гайда):
   - EFFECTIVEYIELD, YIELDTOOFFER — **проценты** → доля (÷100);
   - GSPREADBP, ZSPREADBP — **б.п.** → доля (÷10000);
   - ACCRUEDINT — **рубли на одну бумагу** (сверить по живому ответу: у бумаги с номиналом 1000 и
     купоном ~40 НКД должен быть 0–40 ₽, не 0–4000);
   - DURATION в `marketdata_yields` — дни (как и в marketdata) — новое поле дюрации не заводить,
     оставить существующее `duration_years`.
3. `BondUniverseEntry` (модель) и `BondUniverseEntryMapper`: новые свойства + вычисляемое
   `EffectiveGspreadFraction => GspreadOfficialFraction ?? GspreadApproxFraction` (doc-comment:
   «биржевой спред, при его отсутствии — наше приближение по сохранённой кривой»).
   `GspreadApproxFraction` продолжает считаться как сейчас (fallback).
4. Все внутренние потребители спреда банка переключить на `EffectiveGspreadFraction`:
   `RelativeValueSnapshotBuilder` (корпус и медианы корзин), `CandidateRiskSignalService`
   (ось спреда), `ReplacementEndpoints` (dev кандидатов), скринер/DTO `/api/universe` — поле DTO
   `gspreadApproxFraction` не переименовывать (фронт зеркалит literal), но наполнять effective;
   в DTO добавить `gspreadSource: "moex" | "approx" | null` (additive).
5. История: `BondUniverseRefreshService` при срезе после 19:00 МСК писать новые колонки
   (gspread_effective_fraction = EffectiveGspreadFraction на момент среза).
6. Аллокация market/recommended: цена лота = `Price%/100 × FaceValue + AccruedRub` (+комиссия);
   при `accrued_rub IS NULL` — текущее поведение (0) с сохранением пометки в doc-comment.

### C. Frontend (минимальный)

1. `api/types.ts`: новые nullable-поля DTO (accruedRub, issueSize, yieldToOfferFraction,
   gspreadSource) — зеркально бэкенду.
2. Скринер/сравнивалка: у значения G-спреда тултип «источник: биржевой / расчётный» по
   `gspreadSource`. Без перестройки таблиц.

### Тесты

- Юнит: парсер marketdata_yields на новой фикстуре (б.п.→доля, %→доля, отсутствующая строка →
  null); `EffectiveGspreadFraction` fallback; маппер ACCRUEDINT.
- Интеграционные: `/api/universe` отдаёт новые поля; аллокация с НКД (эталонный расчёт руками:
  цена 95%, номинал 1000, НКД 12.34 ₽, комиссия 0.3% → лот = 962.34 × 1.003 = 965.23 ₽).
- История: после среза в строке дня есть bid/offer/list_level/gspread_effective_fraction.

## Критерии приёмки

- [ ] В `/api/universe` у большинства торгуемых бумаг G-спред биржевой (`gspreadSource="moex"`),
      у остальных — fallback `approx`.
- [ ] Цена лота в аллокации «весь рынок» включает НКД, когда он известен.
- [ ] Строка `bond_universe_history` за день содержит bid, offer, list_level, gspread.
- [ ] Миграция 017 + `MigrationIdempotencyTests` обновлены; существующие тесты не ослаблены;
      `scripts/pre-push-check.sh --all` — зелёный.
