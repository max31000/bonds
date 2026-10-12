-- Этап 08: настройки пользователя (GET/PUT /api/settings, PUT /api/settings/tinvest-token).
-- Single-user продукт (spec §2) — таблица физически допускает несколько строк (одна на user_id),
-- но реально существует ровно одна строка владельца, как и users/accounts.
--
-- tinvest_token_encrypted хранит токен, зашифрованный Microsoft.AspNetCore.DataProtection
-- (IDataProtectionProvider) — НЕ plain text (spec §11: "токен не логировать, не отдавать на
-- фронт"). Используется только как override личного использования. Приоритет источника токена
-- для синка: ENV (TInvest:Token) для прод/CI, эта запись — override, если задана (см.
-- ITInvestTokenProvider в Bonds.Infrastructure/Services).
--
-- Пороги Signals Engine (spec §8) персистируются per-user, чтобы PUT /api/settings мог их менять
-- без редеплоя. NULL-колонки означают "использовать дефолт SignalEngineOptions" (этап 07).
CREATE TABLE IF NOT EXISTS user_settings (
    id                              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id                         BIGINT UNSIGNED NOT NULL,

    tinvest_token_encrypted         TEXT            NULL,
    tinvest_token_last4             VARCHAR(4)      NULL,

    upcoming_event_days_threshold   INT             NULL,
    uninvested_cash_threshold_rub   DECIMAL(18,2)   NULL,
    uninvested_cash_lookback_days   INT             NULL,
    yield_below_alternative_bps     INT             NULL,
    maturity_window_days            INT             NULL,
    default_max_concentration_pct   DECIMAL(5,2)    NULL,
    duration_drift_tolerance_years  DECIMAL(5,2)    NULL,

    created_at                      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_user_settings_user_id (user_id),
    CONSTRAINT fk_user_settings_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
