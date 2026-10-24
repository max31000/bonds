-- Задача 68: ИИ-агенты (наблюдатель и исследователь через подключение claude_code). Агент НЕ торгует и не меняет настройки.
-- agent_settings     - настройки агента пользователя: enabled (по умолчанию 0 - выключен), model (алиас CLI для рутины, например sonnet), monthly_model (ежемесячный обзор, opus), daily_run_limit (запусков в сутки МСК)
-- agent_runs         - журнал запусков агента: вид задачи, время, код выхода, стоимость (USD, оценка CLI), токены, текст ответа (недоверенный текст), ошибка
-- trade_plans.source - кто предложил план: NULL - правила или пользователь, agent - инструмент propose_plan агента (план всегда ждёт решения пользователя)
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS agent_settings (
    user_id          BIGINT UNSIGNED NOT NULL,
    enabled          TINYINT(1)      NOT NULL DEFAULT 0,
    model            VARCHAR(64)     NOT NULL DEFAULT 'sonnet',
    monthly_model    VARCHAR(64)     NOT NULL DEFAULT 'opus',
    daily_run_limit  INT             NOT NULL DEFAULT 5,
    updated_at       DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    PRIMARY KEY (user_id),
    CONSTRAINT fk_agent_settings_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS agent_runs (
    id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id      BIGINT UNSIGNED NOT NULL,
    kind         VARCHAR(32)     NOT NULL,
    model        VARCHAR(64)     NULL,
    question     VARCHAR(1000)   NULL,
    started      DATETIME        NOT NULL,
    finished     DATETIME        NULL,
    exit_code    INT             NULL,
    cost_usd     DECIMAL(12, 6)  NULL,
    tokens       BIGINT          NULL,
    result_text  MEDIUMTEXT      NULL,
    error        VARCHAR(1000)   NULL,

    PRIMARY KEY (id),
    KEY idx_agent_runs_user_started (user_id, started),
    CONSTRAINT fk_agent_runs_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE trade_plans ADD COLUMN source VARCHAR(16) NULL AFTER decided_via;
