# Задача 43 — Эмитенты в банке облигаций + флаги дефолтов с карточек MOEX

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md).
>
> **Зависимости:** задача 42 (ветка стартует от её HEAD; конфликтующие файлы —
> `MoexIssClient`, `BondUniverseRefreshService`, миграции).
> **Порядок в волне:** строго после 42.
>
> **Рамки (что НЕ делать):** не использовать новые данные в светофоре/скоринге (задача 46);
> не подключать ГИР БО (задача 45); не менять RV и замены.

---

## Проблема

В банке облигаций нет эмитента: ни концентрации по эмитенту на рыночном слое, ни ключа (ИНН)
для будущей связки с отчётностью ГИР БО. При этом MOEX ISS бесплатно отдаёт (живые пробы
2026-10-15):

- bulk: `GET /iss/securities.json?engine=stock&market=bonds&iss.only=securities&securities.columns=secid,emitent_id,emitent_title,emitent_inn&start=N`
  — все облигации постранично по 100 строк (~34 страницы на весь банк);
- карточка бумаги: `GET /iss/securities/{secid}.json?iss.only=description` — пары name/value, среди
  них **HASDEFAULT (0/1), HASTECHNICALDEFAULT (0/1)**, ISQUALIFIEDINVESTORS, COUPON_BENCHMARK,
  COUPON_BENCHMARK_SPREAD, EMITTER_ID, INITIALFACEVALUE, EARLYREPAYMENT.

История дефолтов эмитента (по всем его выпускам) — сильнейший бесплатный кредитный сигнал
(по исследованию на 962 эмитентах РФ дефолт-история и уровень листинга — топ-предикторы).

## Что сделать

### A. Эмитент в bond_universe (bulk, дёшево)

1. Миграция `018_universe_emitents_and_security_details.sql` (+ `MigrationIdempotencyTests`):
   - `bond_universe` добавить `emitent_id INT NULL`, `emitent_inn VARCHAR(12) NULL`,
     `emitent_title VARCHAR(512) NULL`;
   - новая таблица `security_details` (см. B).
2. `MoexIssClient`: метод bulk-справочника с пагинацией `start=0,100,…` до пустой страницы
   (образец пагинации — постраничная загрузка купонов в этом же клиенте). Фикстура — реальный
   JSON одной страницы.
3. `BondUniverseRefreshService`: отдельный шаг «обогащение эмитентами» **раз в сутки** (первый
   часовой тик после 09:50 или при пустых emitent_* в банке), не в каждом часовом цикле. Маппинг
   по SECID; бумага без совпадения — поля null (не падать).
4. `/api/universe` DTO: `emitentTitle`, `emitentInn` (additive); фронт `types.ts` зеркально.

### B. Карточки бумаг: таблица security_details (персистентный кэш)

`bond_universe` перезаписывается каждый час — флаги карточек хранить отдельно.

1. Таблица `security_details`: `secid VARCHAR(36) PK`, `isin VARCHAR(12) NULL`,
   `emitent_id INT NULL`, `has_default TINYINT(1) NULL`, `has_technical_default TINYINT(1) NULL`,
   `is_qualified_investors TINYINT(1) NULL`, `coupon_benchmark VARCHAR(64) NULL`,
   `coupon_benchmark_spread_fraction DECIMAL(12,8) NULL` (вход в **процентах** → доля ÷100),
   `early_repayment TINYINT(1) NULL`, `fetched_at DATETIME NOT NULL`. MySQL-стиль гайда.
2. Репозиторий `SecurityDetailsRepository` по образцу `InstrumentRepository` (батчевый upsert
   чанками, DynamicParameters).
3. Фоновое обогащение в sync-цикле (образец устойчивости — `BondUniverseRefreshService`):
   за один тик обрабатывать до **200** бумаг, приоритет: (1) инструменты портфеля и watchlist,
   (2) бумаги банка без записи в `security_details`, (3) записи старше **30 дней**. Ошибка по
   одной бумаге = LogWarning + пропуск. Так весь банк (~3400) прогревается за ~17 дней, портфель —
   в первый день; лимиты ISS не напрягаем.
4. Метод репозитория «дефолт-история эмитента»: по `emitent_id` — `MAX(has_default)`,
   `MAX(has_technical_default)` по всем его бумагам в `security_details` (одним SQL, без N+1).
   Потребителей пока не подключать (задача 46) — покрыть только тестом репозитория.

### C. Frontend (минимальный)

1. Скринер: колонка/тултип «Эмитент» (emitentTitle) — только отображение, без фильтров.

### Тесты

- Юнит: парсер bulk-страницы; парсер description-карточки (HASDEFAULT=1, пустые значения,
  COUPON_BENCHMARK_SPREAD %→доля).
- Интеграционные: upsert `security_details` идемпотентен; приоритизация очереди обогащения
  (портфель раньше банка); агрегат дефолт-истории по эмитенту (две бумаги эмитента, у одной
  has_default=1 → агрегат true).

## Критерии приёмки

- [ ] У бумаг банка заполнены emitent_inn/emitent_title (кроме тех, где MOEX их не отдаёт).
- [ ] `security_details` наполняется чанками по 200 с приоритетом портфеля; повторный прогон
      не дублирует строки.
- [ ] Агрегат «эмитент дефолтил» доступен методом репозитория и покрыт тестом.
- [ ] Миграция 018 + `MigrationIdempotencyTests`; `scripts/pre-push-check.sh --all` — зелёный.
