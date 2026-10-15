# Задача 45 — Коннектор ГИР БО: отчётность эмитентов по ИНН

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md).
>
> **Зависимости:** задача 43 (`bond_universe.emitent_inn` — источник списка ИНН).
> **Порядок в волне:** первая в В2; задача 46 потребляет таблицу `issuer_financials` и
> репозиторий из этой задачи.
>
> **Рамки (что НЕ делать):** не считать скоринг и не менять светофор (задача 46); не делать UI
> (задача 47); не качать весь реестр ГИР БО — только эмитенты портфеля/watchlist (см. B.3).

---

## Проблема

Кредитный слой скоркарты (задача 46) требует отчётность эмитентов. Живыми пробами 2026-10-15
подтверждено: ГИР БО ФНС (`bo.nalog.gov.ru`, домен `bo.nalog.ru` отвечает 302 на него) отдаёт
без авторизации JSON (нужен нормальный User-Agent):

1. `GET /advanced-search/organizations/search?query=<ИНН>&page=0` →
   `content[].{id, inn, shortName, ogrn, statusCode, bfo:{period,...}}`;
2. `GET /nbo/organizations/{id}/bfo/` → список подач: `[{id, period, actualBfoDate, isCb, ...}]`;
3. `GET /nbo/bfo/{bfoId}/details` → массив корректировок, у каждой: `balance` (коды строк
   баланса: `current1600`, `previous1600`, `beforePrevious1600`, …), `financialResult`
   (`current2110` выручка, `current2200` прибыль от продаж, `current2330` проценты к уплате,
   `current2400` чистая прибыль, …), `fundsMovement` (ОДДС), `auditReport`, `periodType`,
   `correctionVersion`.

Значения — **тысячи рублей** (Лента 2021: `current2110 = 495877663` ≈ 495.9 млрд ₽ — сходится с
публичной выручкой). В каждой подаче три года (current/previous/beforePrevious). Ограничения:
РСБУ соло (не консолидация), банки/лизинг/страховые отсутствуют (`isCb`, отчитываются в ЦБ),
часть эмитентов скрыла отчётность (пост. №1624), свежесть — годовая.

## Что сделать

### A. Клиент и парсинг

1. `Bonds.Infrastructure/Connectors/GirBo/`: `IGirBoClient` + `GirBoClient` — именованный
   HttpClient (образец — `MoexIssClient.HttpClientName`), BaseAddress `https://bo.nalog.gov.ru`,
   заголовки `User-Agent: BondAnalytics/1.0 (personal portfolio tool)` и `Accept:
   application/json`. Таймаут 30 с. Три метода по эндпоинтам выше. 404/пустой `content` —
   нормальный результат («не найден»), не исключение.
2. Выбор подачи: последняя по `period`; среди корректировок — максимальный
   `correctionVersion`. `periodType` != годового — пропустить (брать только годовые).
3. Парсить только нужные строки (ключи вида `current1600`): 1100, 1200, 1250, 1300, 1370, 1400,
   1410, 1500, 1510, 1600 (баланс); 2110, 2200, 2330, 2400 (ОФР); наличие `auditReport`.
   Остальное не тащить. Из трёх лет подачи формировать до трёх годовых записей (current →
   period, previous → period−1, beforePrevious → period−2) — так одна свежая подача даёт тренд.
4. Фикстуры: реальные (анонимизировать не нужно — данные публичные) JSON-ответы всех трёх
   эндпоинтов в `tests/Bonds.Tests/Fixtures/GirBo/`; тесты клиента — `FakeHandler`, по образцу
   `Moex*PagingTests`.

### B. Хранение и фоновая загрузка

1. Миграция `019_issuer_financials.sql` (+ `MigrationIdempotencyTests`), таблица
   `issuer_financials`:
   `inn VARCHAR(12) NOT NULL`, `period SMALLINT NOT NULL` (год), `source VARCHAR(16) NOT NULL`
   ('girbo'|'manual'), `fetched_at DATETIME NOT NULL`,
   `revenue DECIMAL(20,2) NULL`, `operating_profit DECIMAL(20,2) NULL`,
   `interest_paid DECIMAL(20,2) NULL`, `net_profit DECIMAL(20,2) NULL`,
   `equity DECIMAL(20,2) NULL`, `total_assets DECIMAL(20,2) NULL`,
   `current_assets DECIMAL(20,2) NULL`, `current_liabilities DECIMAL(20,2) NULL`,
   `cash DECIMAL(20,2) NULL`, `long_debt DECIMAL(20,2) NULL`, `short_debt DECIMAL(20,2) NULL`,
   `retained_earnings DECIMAL(20,2) NULL`, `has_audit TINYINT(1) NULL`,
   `UNIQUE KEY uq_issuer_financials (inn, period, source)`.
   **Единицы: рубли** (вход ГИР БО в тыс. ₽ → ×1000 на границе, doc-comment). Связь с
   инструментами — по ИНН-строке (как watchlist по ISIN), без FK.
2. Отдельная таблица `issuer_financials_status`: `inn PK`, `status VARCHAR(16)`
   ('ok'|'not_found'|'hidden'|'cb'|'error'), `checked_at DATETIME` — чтобы не долбить ГИР БО по
   эмитентам, которых там нет ('hidden' — организация есть, подач нет или details пустой;
   'cb' — `isCb=true`).
3. Репозиторий `IssuerFinancialsRepository` (образец — `InstrumentRepository`): upsert записей,
   `GetLatestByInnsAsync` (последние ≤3 года по каждому ИНН, одним запросом), upsert статуса.
4. Фоновый шаг в sync-цикле (образец устойчивости — `BondUniverseRefreshService`): **раз в
   сутки** собрать ИНН эмитентов (а) позиций портфеля, (б) watchlist — через
   `bond_universe.emitent_inn` по ISIN; обновлять те, у кого `checked_at` старше **30 дней** или
   записи нет. Не более 20 ИНН за тик, пауза ≥1 с между эмитентами (бережём ГИР БО). Ошибка по
   одному ИНН — LogWarning + статус 'error', продолжить.

### Тесты

- Юнит: парсинг details (тыс.₽→₽, три года из одной подачи, выбор максимальной корректировки,
  пропуск неполного года); 'not_found'/'hidden'/'cb' статусы.
- Интеграционные: upsert идемпотентен; `GetLatestByInnsAsync` возвращает по 3 года на ИНН;
  manual-записи не затираются girbo-загрузкой (разные source).

## Критерии приёмки

- [ ] После первого суточного тика у эмитентов портфеля с открытой отчётностью в
      `issuer_financials` есть ≤3 годовых записей в рублях; у банков — статус 'cb', у скрытых —
      'hidden'.
- [ ] Повторный тик не перекачивает свежепроверенные ИНН (30-дневный кэш, тест).
- [ ] Миграция 019 + `MigrationIdempotencyTests`; `scripts/pre-push-check.sh --all` — зелёный.
