-- Этап 03: расширение таблицы users из этапа 02 — базовая валюта пользователя (spec §11, RUB-only MVP).
-- MySQL 8.0 не поддерживает синтаксис ADD COLUMN IF NOT EXISTS (это MariaDB-расширение) —
-- идемпотентность здесь обеспечивается реестром _migrations в MigrationRunner (файл применяется
-- максимум один раз), как и для всех прочих миграций этого проекта (см. 001/002).
ALTER TABLE users
    ADD COLUMN base_currency VARCHAR(3) NOT NULL DEFAULT 'RUB' AFTER last_name;
