-- Задача 55: валюта как актив (юани и т.п.) - ОТДЕЛЬНЫЙ домен по образцу фондов (023), не смешивается с облигациями.
-- Единицы (контракт docs/CODEBASE-GUIDE.md):
--   quantity                  - ЕДИНИЦЫ валюты (юани), НЕ лоты
--   *_price_rub, close_rub    - РУБЛИ ЗА 1 ЕДИНИЦУ валюты (для JPY и подобных MOEX котирует за FACEVALUE единиц - пересчёт в
--                               рубли за 1 единицу делается при записи истории курса, см. face_value)
--   face_value                - MOEX FACEVALUE: за сколько единиц валюты MOEX котирует CLOSE (CNY=1, JPY=100)
--   amount_rub                - знак как отдал брокер, РУБЛИ (после проверки валюты платежа), НЕ переписывать
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS currencies (
    id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    figi        VARCHAR(32)     NOT NULL,
    iso_code    VARCHAR(8)      NOT NULL,
    ticker      VARCHAR(32)     NULL,
    name        VARCHAR(255)    NULL,
    moex_secid  VARCHAR(32)     NULL,
    lot         DECIMAL(20,6)   NULL,
    face_value  DECIMAL(20,6)   NOT NULL DEFAULT 1,
    created_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_currencies_figi (figi),
    UNIQUE KEY uq_currencies_iso (iso_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS currency_positions (
    id                      BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id              BIGINT UNSIGNED NOT NULL,
    currency_id             BIGINT UNSIGNED NOT NULL,
    quantity                DECIMAL(20,6)   NOT NULL,
    avg_purchase_price_rub  DECIMAL(20,6)   NULL,
    current_price_rub       DECIMAL(20,6)   NULL,
    price_as_of             DATETIME        NULL,
    updated_at              DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_currency_positions_account_currency (account_id, currency_id),
    KEY idx_currency_positions_currency (currency_id),
    CONSTRAINT fk_currency_positions_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_currency_positions_currency FOREIGN KEY (currency_id)
        REFERENCES currencies (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS currency_operations (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id    BIGINT UNSIGNED NOT NULL,
    currency_id   BIGINT UNSIGNED NULL,
    type          ENUM('Buy','Sell','Tax','Fee','Income') NOT NULL,
    date          DATETIME        NOT NULL,
    amount_rub    DECIMAL(20,6)   NOT NULL,
    quantity      DECIMAL(20,6)   NULL,
    external_id   VARCHAR(64)     NOT NULL,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_currency_operations_external_id (external_id),
    KEY idx_currency_operations_account_date (account_id, date),
    KEY idx_currency_operations_currency (currency_id),
    CONSTRAINT fk_currency_operations_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_currency_operations_currency FOREIGN KEY (currency_id)
        REFERENCES currencies (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Дневные закрытия курса (MOEX CETS) - РУБЛИ за 1 единицу валюты (уже поделено на face_value).
CREATE TABLE IF NOT EXISTS currency_rate_history (
    currency_id  BIGINT UNSIGNED NOT NULL,
    trade_date   DATE            NOT NULL,
    close_rub    DECIMAL(20,6)   NOT NULL,

    UNIQUE KEY uq_currency_rate_history (currency_id, trade_date),
    CONSTRAINT fk_currency_rate_history_currency FOREIGN KEY (currency_id)
        REFERENCES currencies (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
