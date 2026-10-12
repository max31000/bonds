-- Этап 02: таблица пользователей для Telegram-авторизации.
-- Продукт single-user (см. spec §2) — таблица физически допускает несколько строк,
-- но бизнес-правило allowlist (Telegram:OwnerId) гарантирует, что реально
-- создаётся/используется ровно одна запись — запись владельца.

CREATE TABLE IF NOT EXISTS users (
    id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    telegram_id BIGINT          NOT NULL,
    username    VARCHAR(255)    NULL,
    first_name  VARCHAR(255)    NULL,
    last_name   VARCHAR(255)    NULL,
    created_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    UNIQUE KEY uq_users_telegram_id (telegram_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
