-- Этап 03: доменная схема Bond Portfolio Analytics (spec §5).
-- Разрез: справочные данные инструмента (instruments, *_schedules) — общие для всех,
-- рыночные котировки (market_quotes, yield_curve_snapshots) — временные ряды,
-- позиции/операции (accounts, positions, operations) — cost basis и журнал пользователя.
-- Денежные суммы — DECIMAL(18,6) (spec §11 — RUB как базовая валюта).
-- ВНИМАНИЕ: в комментариях этого файла не использовать символ "точка с запятой" — MigrationRunner
-- разбивает скрипт на отдельные statements наивным split по этому символу, без учёта SQL-комментариев.

-- 1. instruments — справочник выпуска.
CREATE TABLE IF NOT EXISTS instruments (
    id                      BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    isin                    VARCHAR(12)     NOT NULL,
    secid                   VARCHAR(32)     NULL,
    figi                    VARCHAR(32)     NULL,
    issuer                  VARCHAR(255)    NULL,
    sector                  VARCHAR(100)    NULL,
    face_value              DECIMAL(18,6)   NOT NULL DEFAULT 0,
    currency                VARCHAR(3)      NOT NULL DEFAULT 'RUB',
    coupon_type             ENUM('Fixed','Floating','Indexed') NOT NULL DEFAULT 'Fixed',
    has_amortization        TINYINT(1)      NOT NULL DEFAULT 0,
    has_offers              TINYINT(1)      NOT NULL DEFAULT 0,
    maturity_date           DATE            NOT NULL,
    -- §4.4: пометка "данные неполные" — не падать, не подставлять нули молча.
    data_incomplete         TINYINT(1)      NOT NULL DEFAULT 0,
    -- §11: MVP — портфель рублёвый, не-RUB бумаги помечаются и исключаются из агрегатов.
    is_out_of_scope_currency TINYINT(1)     NOT NULL DEFAULT 0,
    created_at              DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at              DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_instruments_isin (isin),
    KEY idx_instruments_secid (secid),
    KEY idx_instruments_figi (figi)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 2. coupon_schedules — график купонов (источник истины MOEX bondization, spec §4.2).
CREATE TABLE IF NOT EXISTS coupon_schedules (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    instrument_id BIGINT UNSIGNED NOT NULL,
    coupon_date   DATE            NOT NULL,
    value_rub     DECIMAL(18,6)   NULL,
    period_days   INT             NULL,
    -- Флоатер: известен только купон до ближайшего пересчёта ставки (spec §4.4/§6).
    is_known      TINYINT(1)      NOT NULL DEFAULT 1,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_coupon_schedules_instrument_date (instrument_id, coupon_date),
    CONSTRAINT fk_coupon_schedules_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 3. amortization_schedules — график амортизации (источник истины MOEX, spec §4.1/§4.4).
CREATE TABLE IF NOT EXISTS amortization_schedules (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    instrument_id BIGINT UNSIGNED NOT NULL,
    date          DATE            NOT NULL,
    amount_rub    DECIMAL(18,6)   NOT NULL,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_amortization_schedules_instrument_date (instrument_id, date),
    CONSTRAINT fk_amortization_schedules_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 4. offer_schedules — график оферт put/call (spec §4.4/§7.3).
CREATE TABLE IF NOT EXISTS offer_schedules (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    instrument_id BIGINT UNSIGNED NOT NULL,
    date          DATE            NOT NULL,
    offer_type    ENUM('Put','Call') NOT NULL,
    is_executed   TINYINT(1)      NOT NULL DEFAULT 0,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_offer_schedules_instrument_date_type (instrument_id, date, offer_type),
    CONSTRAINT fk_offer_schedules_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 5. market_quotes — временной ряд котировок. Текущая цена/НКД приоритетно из T-Invest,
-- историческое/справочное — из MOEX (plan/00 §4 "Разделение источников").
CREATE TABLE IF NOT EXISTS market_quotes (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    instrument_id BIGINT UNSIGNED NOT NULL,
    as_of         DATE            NOT NULL,
    clean_price   DECIMAL(18,6)   NULL,
    dirty_price   DECIMAL(18,6)   NULL,
    accrued       DECIMAL(18,6)   NULL,
    volume        DECIMAL(18,6)   NULL,
    source        ENUM('TInvest','Moex') NOT NULL,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_market_quotes_instrument_asof_source (instrument_id, as_of, source),
    KEY idx_market_quotes_instrument_asof (instrument_id, as_of),
    CONSTRAINT fk_market_quotes_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 6. yield_curve_snapshots — параметры Gcurve/КБД на дату (Нельсон-Сигель-Свенссон, spec §4.2/§6).
CREATE TABLE IF NOT EXISTS yield_curve_snapshots (
    id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    as_of      DATE            NOT NULL,
    b1         DECIMAL(18,8)   NOT NULL,
    b2         DECIMAL(18,8)   NOT NULL,
    b3         DECIMAL(18,8)   NOT NULL,
    t1         DECIMAL(18,8)   NOT NULL,
    g1         DECIMAL(18,8)   NOT NULL,
    g2         DECIMAL(18,8)   NOT NULL,
    g3         DECIMAL(18,8)   NOT NULL,
    g4         DECIMAL(18,8)   NOT NULL,
    g5         DECIMAL(18,8)   NOT NULL,
    g6         DECIMAL(18,8)   NOT NULL,
    g7         DECIMAL(18,8)   NOT NULL,
    g8         DECIMAL(18,8)   NOT NULL,
    g9         DECIMAL(18,8)   NOT NULL,
    created_at DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_yield_curve_snapshots_as_of (as_of)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 7. accounts — брокерский счёт (агрегат портфеля). MVP — один счёт на пользователя
-- (spec §2), UserId — точка расширения под мультисчёт (plan/00 §11), не реализуется.
CREATE TABLE IF NOT EXISTS accounts (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id           BIGINT UNSIGNED NOT NULL,
    broker_account_id VARCHAR(64)     NULL,
    name              VARCHAR(255)    NOT NULL DEFAULT 'Основной счёт',
    created_at        DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    KEY idx_accounts_user (user_id),
    CONSTRAINT fk_accounts_user FOREIGN KEY (user_id)
        REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 8. positions — холдинг пользователя (cost basis), источник истины T-Invest (spec §4.1/§5).
CREATE TABLE IF NOT EXISTS positions (
    id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id         BIGINT UNSIGNED NOT NULL,
    instrument_id      BIGINT UNSIGNED NOT NULL,
    quantity           DECIMAL(18,6)   NOT NULL DEFAULT 0,
    avg_purchase_price DECIMAL(18,6)   NOT NULL DEFAULT 0,
    accrued            DECIMAL(18,6)   NOT NULL DEFAULT 0,
    data_incomplete    TINYINT(1)      NOT NULL DEFAULT 0,
    updated_at         DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_positions_account_instrument (account_id, instrument_id),
    KEY idx_positions_instrument (instrument_id),
    CONSTRAINT fk_positions_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_positions_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 9. operations — журнал операций, истина для XIRR (spec §5/§6.9).
-- external_id unique — идемпотентность повторного синка (plan/03 §B/§C).
CREATE TABLE IF NOT EXISTS operations (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id    BIGINT UNSIGNED NOT NULL,
    instrument_id BIGINT UNSIGNED NULL,
    type          ENUM('Buy','Sell','Coupon','Amortization','Redemption','Tax','Fee') NOT NULL,
    date          DATETIME        NOT NULL,
    amount_rub    DECIMAL(18,6)   NOT NULL,
    quantity      DECIMAL(18,6)   NULL,
    external_id   VARCHAR(128)    NOT NULL,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_operations_external_id (external_id),
    KEY idx_operations_account_date (account_id, date),
    KEY idx_operations_instrument (instrument_id),
    CONSTRAINT fk_operations_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_operations_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 10. projected_cash_flows — производная проекция поступлений (заполняется этапом 06).
CREATE TABLE IF NOT EXISTS projected_cash_flows (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    position_id   BIGINT UNSIGNED NOT NULL,
    instrument_id BIGINT UNSIGNED NOT NULL,
    date          DATE            NOT NULL,
    flow_type     ENUM('Coupon','Amortization','Redemption') NOT NULL,
    gross_rub     DECIMAL(18,6)   NOT NULL DEFAULT 0,
    tax_rub       DECIMAL(18,6)   NOT NULL DEFAULT 0,
    net_rub       DECIMAL(18,6)   NOT NULL DEFAULT 0,
    is_estimated  TINYINT(1)      NOT NULL DEFAULT 0,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    KEY idx_projected_cash_flows_position (position_id),
    KEY idx_projected_cash_flows_account_date (instrument_id, date),
    CONSTRAINT fk_projected_cash_flows_position FOREIGN KEY (position_id)
        REFERENCES positions (id) ON DELETE CASCADE,
    CONSTRAINT fk_projected_cash_flows_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 11. portfolio_value_snapshots — ежедневный снимок NAV/XIRR (заполняется этапом 07, spec §9).
CREATE TABLE IF NOT EXISTS portfolio_value_snapshots (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id       BIGINT UNSIGNED NOT NULL,
    as_of            DATE            NOT NULL,
    market_value_rub DECIMAL(18,6)   NOT NULL DEFAULT 0,
    xirr_to_date     DECIMAL(18,8)   NULL,
    invested_rub     DECIMAL(18,6)   NOT NULL DEFAULT 0,
    created_at       DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_portfolio_value_snapshots_account_asof (account_id, as_of),
    CONSTRAINT fk_portfolio_value_snapshots_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 12. signals — сгенерированные триггеры (заполняется этапом 07, spec §8).
CREATE TABLE IF NOT EXISTS signals (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id       BIGINT UNSIGNED NOT NULL,
    type             ENUM('UpcomingCoupon','UpcomingAmortization','UpcomingRedemption','UpcomingOffer',
                          'FloaterRateReset','UninvestedCashThreshold','YieldBelowAlternative',
                          'ConcentrationLimitBreached','DurationDriftFromTarget','LowLiquidityWarning') NOT NULL,
    severity         ENUM('Info','Warning','Critical') NOT NULL DEFAULT 'Info',
    position_id      BIGINT UNSIGNED NULL,
    instrument_id    BIGINT UNSIGNED NULL,
    suggested_action VARCHAR(500)    NULL,
    date             DATE            NOT NULL,
    is_read          TINYINT(1)      NOT NULL DEFAULT 0,
    created_at       DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    KEY idx_signals_account_isread (account_id, is_read),
    CONSTRAINT fk_signals_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_signals_position FOREIGN KEY (position_id)
        REFERENCES positions (id) ON DELETE SET NULL,
    CONSTRAINT fk_signals_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 13. target_allocations — целевые доли/лимиты концентрации (опционально, spec §5/§8).
CREATE TABLE IF NOT EXISTS target_allocations (
    id                         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id                 BIGINT UNSIGNED NOT NULL,
    issuer                     VARCHAR(255)    NULL,
    target_share_percent       DECIMAL(5,2)    NULL,
    max_concentration_percent  DECIMAL(5,2)    NULL,
    target_duration_years      DECIMAL(8,4)    NULL,
    created_at                 DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                 DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    KEY idx_target_allocations_account (account_id),
    CONSTRAINT fk_target_allocations_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
