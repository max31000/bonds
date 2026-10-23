-- Задача 64: Telegram-бот (подтверждение планов и команды). Заявки брокерам по-прежнему НЕ отправляются.
-- bot_state          - служебные пары ключ-значение бота (offset getUpdates, чтобы после рестарта не обрабатывать апдейты повторно)
-- trade_plans.bot_nonce - случайные 8 байт base64url, уходят в callback_data кнопок плана (защита от подставленных и устаревших нажатий)
-- user_settings.execution_paused - 1 = команда /stop: правила не создают AutoApproved (только PendingApproval), в фазе 3 исполнитель ничего не отправляет
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS bot_state (
    `key`       VARCHAR(64)  NOT NULL,
    `value`     VARCHAR(255) NOT NULL,
    updated_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (`key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE trade_plans ADD COLUMN bot_nonce VARCHAR(16) NULL AFTER decided_via;

ALTER TABLE user_settings ADD COLUMN execution_paused TINYINT(1) NOT NULL DEFAULT 0;
