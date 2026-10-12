-- Задача 26 часть B: банк облигаций MOEX - дешёвый снимок всей рыночной вселенной со статистикой,
-- которую биржа отдаёт готовой (YIELD/DURATION/обороты/листинг). Двухъярусная архитектура (не
-- нарушать): эта таблица НЕ прогоняется через точный движок BondMetricsCalculator - только
-- избранные бумаги (задача 27). bond_universe - текущий снимок upsert по secid, bond_universe_history
-- - дневные срезы для трендов/медиан по корзинам (задача 30 B3), retention ~400 дней.
-- Единицы (см. doc-comment BondUniverseEntry): yield_fraction - ДОЛЯ (MOEX YIELD в % / 100),
-- duration_years - ГОДЫ (MOEX DURATION в днях / 365), *_percent - % от номинала как отдаёт MOEX.
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner
-- делит файл по этому символу без учёта SQL-комментариев).

CREATE TABLE IF NOT EXISTS bond_universe (
    id                          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    secid                       VARCHAR(32)     NOT NULL,
    isin                        VARCHAR(12)     NULL,
    short_name                  VARCHAR(255)    NULL,
    sec_name                    VARCHAR(255)    NULL,
    face_value                  DECIMAL(18,6)   NULL,
    lot_value                   DECIMAL(18,6)   NULL,
    coupon_percent              DECIMAL(8,4)    NULL,
    maturity_date               DATE            NULL,
    offer_date                  DATE            NULL,
    list_level                  TINYINT UNSIGNED NULL,
    -- Простая классификация гос/муни/корп по SECTYPE (см. doc-comment BondUniverseSectorMapper) -
    -- НЕ то же самое, что Instrument.Sector (эмитентский сектор из T-Invest/MOEX поиска).
    sector                      VARCHAR(50)     NULL,
    -- ДОЛЯ (MOEX YIELD приходит в %, делим на 100 - см. doc-comment BondUniverseRefreshService).
    yield_fraction               DECIMAL(10,6)   NULL,
    -- ГОДЫ (MOEX DURATION приходит в днях, делим на 365).
    duration_years               DECIMAL(10,6)   NULL,
    price_percent                DECIMAL(10,4)   NULL,
    turnover_rub                 DECIMAL(18,2)   NULL,
    bid_percent                  DECIMAL(10,4)   NULL,
    offer_percent                DECIMAL(10,4)   NULL,
    num_trades                   INT UNSIGNED    NULL,
    -- Приближённый G-спред = yield_fraction - gcurve(duration_years) по последней сохранённой
    -- кривой (см. doc-comment BondUniverseRefreshService) - НЕ точный движок, приближение.
    gspread_approx_fraction       DECIMAL(10,6)   NULL,
    -- Эвристика по BONDTYPE ISS ("Флоатер") - см. MoexSecurityInfo.LooksLikeFloater. NULL, если
    -- распознать не удалось (BONDTYPE отсутствует).
    is_floater                   TINYINT(1)      NULL,
    updated_at                   DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_bond_universe_secid (secid),
    KEY idx_bond_universe_isin (isin),
    KEY idx_bond_universe_yield (yield_fraction),
    KEY idx_bond_universe_duration (duration_years),
    KEY idx_bond_universe_turnover (turnover_rub)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Дневной срез для трендов/медиан по корзинам (задача 30 B3) - одна строка на (snapshot_date, secid).
-- Retention ~400 дней - чистка старых строк выполняется при каждой записи (BondUniverseRefreshService),
-- не отдельной джобой.
CREATE TABLE IF NOT EXISTS bond_universe_history (
    snapshot_date                DATE            NOT NULL,
    secid                        VARCHAR(32)     NOT NULL,
    yield_fraction                DECIMAL(10,6)   NULL,
    duration_years                DECIMAL(10,6)   NULL,
    gspread_approx_fraction       DECIMAL(10,6)   NULL,
    turnover_rub                  DECIMAL(18,2)   NULL,
    price_percent                 DECIMAL(10,4)   NULL,

    PRIMARY KEY (snapshot_date, secid),
    KEY idx_bond_universe_history_secid (secid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
