-- Задача 61: движение денег T-Invest и торговые метаданные инструментов (данные и чтение, без заявок).
-- account_cash_movements - пополнения и выводы счёта T-Invest (операции Input/Output журнала, которые раньше выбрасывались).
--   amount     - сумма в СВОЕЙ валюте (currency) СО ЗНАКОМ брокера (плюс - пополнение, минус - вывод)
--   amount_rub - та же сумма в РУБЛЯХ (рубль - как есть, иначе по курсу дня bonds), NULL - курса на дату не нашлось
-- instrument_trading_meta - как и чем торгуется бумага. Единицы:
--   lot                 - штук в лоте
--   min_price_increment - шаг цены в ВАЛЮТЕ ЦЕНЫ за 1 бумагу (рубли для рублёвых бумаг). Облигации T-Invest отдают шаг в ПУНКТАХ
--                         (процент номинала), сюда он пишется УЖЕ в рублях (пункты / 100 x номинал), сырой шаг в пунктах - в
--                         min_price_increment_points (NULL у всего, что не облигация)
--   pays_distributions  - фонды: были ли выплаты за последние 12 месяцев (NULL - неизвестно или не фонд)
--   its_twin            - Freedom: существует котировочный двойник base.ITS (торговать нужно не им, он только котировочный), NULL - не проверялось
-- instrument_key - ISIN, иначе тикер. Метаданные общие для всех пользователей (свойства бумаги, не счёта).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS account_cash_movements (
    id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id   BIGINT UNSIGNED NOT NULL,
    external_id  VARCHAR(64)     NOT NULL,
    op_date      DATETIME        NOT NULL,
    type         VARCHAR(16)     NOT NULL,
    amount_rub   DECIMAL(20,4)   NULL,
    currency     CHAR(3)         NOT NULL,
    amount       DECIMAL(20,4)   NOT NULL,
    created_at   DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_account_cash_movements_account_external (account_id, external_id),
    KEY idx_account_cash_movements_account_date (account_id, op_date),
    CONSTRAINT fk_account_cash_movements_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS instrument_trading_meta (
    id                          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    broker                      VARCHAR(16)     NOT NULL,
    instrument_key              VARCHAR(64)     NOT NULL,
    uid                         VARCHAR(64)     NULL,
    figi                        VARCHAR(32)     NULL,
    ticker                      VARCHAR(32)     NOT NULL,
    class_code                  VARCHAR(16)     NULL,
    lot                         DECIMAL(20,6)   NOT NULL,
    min_price_increment         DECIMAL(20,9)   NOT NULL,
    min_price_increment_points  DECIMAL(20,9)   NULL,
    currency                    CHAR(3)         NOT NULL,
    api_trade_available         TINYINT(1)      NOT NULL DEFAULT 0,
    buy_available               TINYINT(1)      NOT NULL DEFAULT 0,
    sell_available              TINYINT(1)      NOT NULL DEFAULT 0,
    pays_distributions          TINYINT(1)      NULL,
    its_twin                    TINYINT(1)      NULL,
    updated_at                  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_instrument_trading_meta_broker_key (broker, instrument_key),
    KEY idx_instrument_trading_meta_ticker (broker, ticker)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
