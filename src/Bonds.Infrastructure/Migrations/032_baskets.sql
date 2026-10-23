-- Задача 62: корзины - целевая структура капитала. Удаляются target_allocations (индивидуальные лимиты эмитента: лимит теперь один,
-- user_settings.default_max_concentration_percent).
-- baskets - корзина пользователя внутри одного из двух самостоятельных портфелей:
--   portfolio     - rub (T-Invest, валюта портфеля рубль) или usd (Freedom, валюта портфеля доллар)
--   target_weight - ДОЛЯ (0.15 = 15 процентов) от СВОЕГО портфеля, NULL - вне целей (temporary и hold), база доли их не включает
--   band          - допуск отклонения от цели, ДОЛЯ (абсолютная, в долях портфеля)
--   hard_band     - жёсткий допуск, ДОЛЯ, строго больше band
--   liquidity_tier- 0..3 (0 - самая ликвидная)
--   fill_rule     - fixed, engine или hold
--   min_trade     - минимальная сделка в ВАЛЮТЕ портфеля
-- basket_members - члены корзины:
--   instrument_key - ISIN или тикер (для match_rule = asset_class - имя класса актива, например bond)
--   sub_weight     - ДОЛЯ внутри корзины (для fixed), NULL - не задана
--   match_rule     - instrument или asset_class
-- Инвариант "позиция ровно в одной корзине" обеспечивает сервис (явный член, иначе правило класса, иначе системная корзина).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS baskets (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id         BIGINT UNSIGNED NOT NULL,
    portfolio       VARCHAR(8)      NOT NULL,
    code            VARCHAR(32)     NOT NULL,
    title           VARCHAR(128)    NOT NULL,
    target_weight   DECIMAL(9,6)    NULL,
    band            DECIMAL(9,6)    NOT NULL DEFAULT 0.050000,
    hard_band       DECIMAL(9,6)    NOT NULL DEFAULT 0.100000,
    liquidity_tier  TINYINT         NOT NULL DEFAULT 0,
    fill_rule       VARCHAR(8)      NOT NULL DEFAULT 'hold',
    auto_buy        TINYINT(1)      NOT NULL DEFAULT 0,
    auto_sell       TINYINT(1)      NOT NULL DEFAULT 0,
    min_trade       DECIMAL(20,4)   NOT NULL DEFAULT 0,
    sort_order      INT             NOT NULL DEFAULT 0,
    created_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_baskets_user_code (user_id, code),
    KEY idx_baskets_user_portfolio (user_id, portfolio, sort_order),
    CONSTRAINT fk_baskets_user FOREIGN KEY (user_id)
        REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS basket_members (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    basket_id       BIGINT UNSIGNED NOT NULL,
    broker          VARCHAR(16)     NOT NULL,
    instrument_key  VARCHAR(64)     NOT NULL,
    sub_weight      DECIMAL(9,6)    NULL,
    match_rule      VARCHAR(16)     NOT NULL DEFAULT 'instrument',

    PRIMARY KEY (id),
    UNIQUE KEY uq_basket_members_key (basket_id, broker, instrument_key, match_rule),
    CONSTRAINT fk_basket_members_basket FOREIGN KEY (basket_id)
        REFERENCES baskets (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

DROP TABLE IF EXISTS target_allocations;
