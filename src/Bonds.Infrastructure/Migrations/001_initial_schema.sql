-- Этап 01: проверочная миграция — фундамент для MigrationRunner.
-- Доменные таблицы (instruments, positions, operations, ...) добавляются в этапе 03.

CREATE TABLE IF NOT EXISTS schema_version (
    id          INT           NOT NULL AUTO_INCREMENT,
    description VARCHAR(255)  NOT NULL,
    applied_at  DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO schema_version (description) VALUES ('initial schema — foundation scaffold (stage 01)');
