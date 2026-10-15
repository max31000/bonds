-- Задача 43: эмитент в банке облигаций (bulk ISS /iss/securities.json) и персистентный кэш карточек
-- бумаг MOEX (флаги дефолтов, квал-инвесторы, привязка купона к бенчмарку).
-- Единицы (контракт docs/CODEBASE-GUIDE.md): coupon_benchmark_spread_fraction - ДОЛЯ (MOEX
-- COUPON_BENCHMARK_SPREAD приходит в процентных пунктах, например 1.90, делим на 100, получаем 0.019).
-- Флаги has_default/has_technical_default/is_qualified_investors/early_repayment - 0/1 как отдаёт
-- карточка (NULL - карточка не содержит поле, это НЕ то же самое, что 0). fetched_at - UTC.
-- bond_universe перезаписывается каждый час (UpsertSnapshotBatch не трогает emitent_* колонки),
-- поэтому всё, что приходит не из часового снимка, хранится отдельно (emitent_* заполняются
-- отдельным UPDATE раз в сутки, карточки - в security_details).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner
-- делит файл по этому символу без учёта SQL-комментариев).

ALTER TABLE bond_universe
    ADD COLUMN emitent_id    INT          NULL AFTER zspread_official_fraction,
    ADD COLUMN emitent_inn   VARCHAR(12)  NULL AFTER emitent_id,
    ADD COLUMN emitent_title VARCHAR(512) NULL AFTER emitent_inn,
    ADD KEY idx_bond_universe_emitent (emitent_id);

-- Связь с bond_universe/instruments - по строке secid (запись может появиться до появления бумаги
-- в справочнике), без FK. emitent_id - EMITTER_ID карточки (тот же справочник эмитентов MOEX, что
-- emitent_id в bulk-справочнике), по нему считается дефолт-история эмитента по всем его выпускам.
CREATE TABLE IF NOT EXISTS security_details (
    secid                             VARCHAR(36)   NOT NULL,
    isin                              VARCHAR(12)   NULL,
    emitent_id                        INT           NULL,
    has_default                       TINYINT(1)    NULL,
    has_technical_default             TINYINT(1)    NULL,
    is_qualified_investors            TINYINT(1)    NULL,
    coupon_benchmark                  VARCHAR(64)   NULL,
    coupon_benchmark_spread_fraction  DECIMAL(12,8) NULL,
    early_repayment                   TINYINT(1)    NULL,
    fetched_at                        DATETIME      NOT NULL,

    PRIMARY KEY (secid),
    KEY idx_security_details_emitent (emitent_id),
    KEY idx_security_details_fetched (fetched_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
