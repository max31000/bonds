-- Задача 58: «Подключения» - единое расширяемое хранилище секретов пользователя (токены брокеров и ИИ-провайдеров).
-- Новый провайдер добавляется записью в реестр CredentialProviders (Bonds.Core) без миграций.
--
-- secret_encrypted - DataProtection шифротекст. Обычные провайдеры - purpose bonds.credentials.v1.{provider}, открытый текст JSON полей.
-- Легаси-исключение tinvest_read - тот же purpose, что у user_settings.tinvest_token_encrypted (Bonds.TInvestToken.v1),
-- открытый текст - ГОЛАЯ строка токена (не JSON). Поэтому шифротекст из user_settings переносится ниже как есть.
--
-- Колонки user_settings.tinvest_token_* НЕ удаляются (страховочный fallback на время выката, удалим отдельной миграцией).
-- Идемпотентность: CREATE TABLE IF NOT EXISTS + INSERT IGNORE по уникальному ключу (user_id, provider).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS user_credentials (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id           BIGINT UNSIGNED NOT NULL,
    provider          VARCHAR(48)     NOT NULL,
    secret_encrypted  TEXT            NOT NULL,
    secret_last4      VARCHAR(8)      NULL,
    meta_json         JSON            NULL,
    status            VARCHAR(16)     NOT NULL DEFAULT 'unverified',
    status_message    VARCHAR(255)    NULL,
    verified_at       DATETIME        NULL,
    created_at        DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_user_credentials_user_provider (user_id, provider),
    CONSTRAINT fk_user_credentials_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO user_credentials (user_id, provider, secret_encrypted, secret_last4, status)
SELECT user_id, 'tinvest_read', tinvest_token_encrypted, tinvest_token_last4, 'ok'
FROM user_settings
WHERE tinvest_token_encrypted IS NOT NULL AND tinvest_token_encrypted <> '';
