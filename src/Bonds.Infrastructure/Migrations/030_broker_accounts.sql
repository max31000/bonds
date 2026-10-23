-- Задача 60: несколько брокеров. Счёт Freedom живёт рядом со счётом T-Invest в accounts (колонка broker), а его позиции, деньги
-- и журнал - в СВОИХ таблицах broker_* (как фонды и валюта), облигационный движок их не видит.
-- Единицы (контракт docs/CODEBASE-GUIDE.md): во ВСЕХ таблицах broker_* цены и суммы лежат в ВАЛЮТЕ СТРОКИ (колонка currency),
-- НЕ в рублях. Пересчёт в рубли - только на чтении, по курсам bonds (ICurrencyRateLookup).
--   broker_positions.avg_price/last_price - цена за 1 бумагу в валюте бумаги, market_value - стоимость позиции в валюте бумаги
--   broker_cash.amount                   - остаток денег в своей валюте
--   broker_operations.amount             - сумма в валюте операции СО ЗНАКОМ (плюс приход денег, минус расход)
-- asset_class - эвристика по названию бумаги (ETF/UCITS/Fund - etf, иначе stock), брокер класс не отдаёт.
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

-- Существующие строки - счета T-Invest (DEFAULT). Запросы основного счёта (GetPrimaryAccountId/ResolveAccountId) фильтруют broker = 'tinvest'.
ALTER TABLE accounts
    ADD COLUMN broker VARCHAR(16) NOT NULL DEFAULT 'tinvest' AFTER user_id;

ALTER TABLE accounts
    ADD UNIQUE KEY uq_accounts_user_broker_account (user_id, broker, broker_account_id);

CREATE TABLE IF NOT EXISTS broker_positions (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id    BIGINT UNSIGNED NOT NULL,
    ticker        VARCHAR(32)     NOT NULL,
    name          VARCHAR(255)    NOT NULL DEFAULT '',
    asset_class   VARCHAR(16)     NOT NULL DEFAULT 'other',
    currency      CHAR(3)         NOT NULL,
    quantity      DECIMAL(20,6)   NOT NULL,
    avg_price     DECIMAL(20,6)   NOT NULL DEFAULT 0,
    last_price    DECIMAL(20,6)   NOT NULL DEFAULT 0,
    market_value  DECIMAL(20,4)   NOT NULL DEFAULT 0,
    synced_at     DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_broker_positions_account_ticker (account_id, ticker),
    CONSTRAINT fk_broker_positions_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS broker_cash (
    id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id  BIGINT UNSIGNED NOT NULL,
    currency    CHAR(3)         NOT NULL,
    amount      DECIMAL(20,4)   NOT NULL,
    synced_at   DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_broker_cash_account_currency (account_id, currency),
    CONSTRAINT fk_broker_cash_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS broker_operations (
    id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id   BIGINT UNSIGNED NOT NULL,
    external_id  VARCHAR(64)     NOT NULL,
    op_date      DATETIME        NOT NULL,
    type         VARCHAR(16)     NOT NULL,
    ticker       VARCHAR(32)     NULL,
    quantity     DECIMAL(20,6)   NULL,
    price        DECIMAL(20,6)   NULL,
    amount       DECIMAL(20,4)   NOT NULL,
    currency     CHAR(3)         NOT NULL,
    raw_type     VARCHAR(48)     NOT NULL DEFAULT '',
    comment      VARCHAR(512)    NULL,
    created_at   DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_broker_operations_account_external (account_id, external_id),
    KEY idx_broker_operations_account_date (account_id, op_date),
    CONSTRAINT fk_broker_operations_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
