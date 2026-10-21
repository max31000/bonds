-- Задача 51: фонды (БПИФ/ETF) - ОТДЕЛЬНЫЙ домен, не смешивается с instruments/positions/operations облигаций.
-- Единицы (контракт docs/CODEBASE-GUIDE.md):
--   *_rub                     - РУБЛИ (цены - рубли ЗА ОДИН ПАЙ, не проценты номинала как у облигаций)
--   total_expense_fraction    - ДОЛЯ годовых (0.009 = 0.9 процента), конверсия из T-Invest - TInvestNumeric.ExpenseRatioToFraction
--   yield_fraction            - ДОЛЯ (MOEX YIELD в процентах / 100)
--   duration_years            - ГОДЫ (MOEX DURATION в днях / 365)
-- amount_rub в fund_operations - знак как отдал брокер (покупка минус, выплата плюс), НЕ переписывать.
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS funds (
    id                      BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    figi                    VARCHAR(32)     NOT NULL,
    isin                    VARCHAR(12)     NOT NULL,
    ticker                  VARCHAR(32)     NULL,
    secid                   VARCHAR(32)     NULL,
    name                    VARCHAR(255)    NULL,
    management_company      VARCHAR(255)    NULL,
    focus_type              VARCHAR(64)     NULL,
    total_expense_fraction  DECIMAL(9,6)    NULL,
    forecast_method         ENUM('Auto','Distributions','IndexYield','TrailingReturn') NOT NULL DEFAULT 'Auto',
    benchmark_index         VARCHAR(32)     NULL,
    created_at              DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at              DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_funds_figi (figi),
    UNIQUE KEY uq_funds_isin (isin)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS fund_positions (
    id                      BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id              BIGINT UNSIGNED NOT NULL,
    fund_id                 BIGINT UNSIGNED NOT NULL,
    quantity                DECIMAL(20,6)   NOT NULL,
    avg_purchase_price_rub  DECIMAL(20,6)   NOT NULL,
    current_price_rub       DECIMAL(20,6)   NULL,
    price_as_of             DATETIME        NULL,
    updated_at              DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_fund_positions_account_fund (account_id, fund_id),
    KEY idx_fund_positions_fund (fund_id),
    CONSTRAINT fk_fund_positions_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_fund_positions_fund FOREIGN KEY (fund_id)
        REFERENCES funds (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS fund_operations (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id    BIGINT UNSIGNED NOT NULL,
    fund_id       BIGINT UNSIGNED NULL,
    type          ENUM('Buy','Sell','Distribution','Tax','Fee','SecurityIn','SecurityOut') NOT NULL,
    date          DATETIME        NOT NULL,
    amount_rub    DECIMAL(20,6)   NOT NULL,
    quantity      DECIMAL(20,6)   NULL,
    external_id   VARCHAR(64)     NOT NULL,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_fund_operations_external_id (external_id),
    KEY idx_fund_operations_account_date (account_id, date),
    KEY idx_fund_operations_fund (fund_id),
    CONSTRAINT fk_fund_operations_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_fund_operations_fund FOREIGN KEY (fund_id)
        REFERENCES funds (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Дневные закрытия пая (MOEX TQBR) - РУБЛИ за пай.
CREATE TABLE IF NOT EXISTS fund_price_history (
    fund_id     BIGINT UNSIGNED NOT NULL,
    trade_date  DATE            NOT NULL,
    close_rub   DECIMAL(20,6)   NOT NULL,

    UNIQUE KEY uq_fund_price_history (fund_id, trade_date),
    CONSTRAINT fk_fund_price_history_fund FOREIGN KEY (fund_id)
        REFERENCES funds (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Выплаты фонда на ОДИН пай (рыночный факт, не зависит от владения) - рубли за пай.
CREATE TABLE IF NOT EXISTS fund_distributions (
    fund_id               BIGINT UNSIGNED NOT NULL,
    record_date           DATE            NOT NULL,
    payment_date          DATE            NULL,
    amount_per_share_rub  DECIMAL(20,6)   NOT NULL,

    UNIQUE KEY uq_fund_distributions (fund_id, record_date),
    CONSTRAINT fk_fund_distributions_fund FOREIGN KEY (fund_id)
        REFERENCES funds (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Доходность/дюрация облигационных индексов MOEX (RGBI и др.) для прогноза доходности фонда.
CREATE TABLE IF NOT EXISTS market_index_history (
    index_code      VARCHAR(32)     NOT NULL,
    trade_date      DATE            NOT NULL,
    close_value     DECIMAL(20,6)   NULL,
    yield_fraction  DECIMAL(12,8)   NULL,
    duration_years  DECIMAL(12,6)   NULL,

    UNIQUE KEY uq_market_index_history (index_code, trade_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
