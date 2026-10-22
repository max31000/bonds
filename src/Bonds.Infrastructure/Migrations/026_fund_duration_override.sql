-- Задача 57: ручная оценка модифицированной дюрации фонда (для композиции и сценариев ставок на странице Аналитика).
-- Единицы (контракт docs/CODEBASE-GUIDE.md):
--   duration_override_years - ГОДЫ (модифицированная дюрация, оценка владельца), NULL = не задана, берётся оценка по индексу
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

ALTER TABLE funds
    ADD COLUMN duration_override_years DECIMAL(9,4) NULL AFTER benchmark_index;
