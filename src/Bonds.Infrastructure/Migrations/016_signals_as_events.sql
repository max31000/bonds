-- plan/40: «Сигналы» → «События» (текущее состояние вместо append-only лога).
-- 1) Старый лог (~1100 строк-копий одного и того же состояния на каждый синк) отбрасывается целиком:
--    набор активных событий движок пересоберёт сам на ближайшем цикле синка.
DROP TABLE IF EXISTS signals;

-- 2) Новая схема. Идентичность события — (account_id, event_key); event_key = Type|instrument-или-эмитент|дата
--    (дата пустая для «состояний»: концентрация, свободные деньги). NULL-able поля не участвуют в
--    UNIQUE (в MySQL NULL != NULL), поэтому ключ собирается в одну NOT NULL строку.
--    resolved_at — движок перестал производить событие; acknowledged_at — владелец отметил «прочитано»
--    (никогда не сбрасывается пересчётом). Закрытые строки чистятся через 90 дней в цикле синка.
CREATE TABLE signals (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    account_id       BIGINT UNSIGNED NOT NULL,
    event_key        VARCHAR(255)    NOT NULL,
    type             ENUM('UpcomingAmortization','UpcomingRedemption','UpcomingOffer',
                          'FloaterRateReset','UninvestedCashThreshold','ConcentrationLimitBreached') NOT NULL,
    severity         ENUM('Info','Warning','Critical') NOT NULL DEFAULT 'Info',
    instrument_id    BIGINT UNSIGNED NULL,
    subject          VARCHAR(255)    NULL,
    suggested_action VARCHAR(500)    NULL,
    event_date       DATE            NULL,
    first_seen_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at     DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    resolved_at      DATETIME        NULL,
    acknowledged_at  DATETIME        NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_signals_account_key (account_id, event_key),
    KEY idx_signals_account_resolved (account_id, resolved_at),
    CONSTRAINT fk_signals_account FOREIGN KEY (account_id)
        REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT fk_signals_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- 3) Пороги удалённых правил (YieldBelowAlternative / DurationDrift) и окно «прокси-кэша по журналу»
--    больше ничего не делают: свободные деньги берутся из accounts.cash_rub.
ALTER TABLE user_settings
    DROP COLUMN uninvested_cash_lookback_days,
    DROP COLUMN yield_below_alternative_bps,
    DROP COLUMN maturity_window_days,
    DROP COLUMN duration_drift_tolerance_years;
