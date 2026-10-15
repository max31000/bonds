-- Задача 48: горизонт анализа замены - настройка пользователя (годы, шаг 0.5, диапазон 0.5-10
-- валидируется на границе API). Раньше горизонт был фронтовой константой 2 года.
-- DEFAULT 2.00 сохраняет прежнее поведение для уже существующих строк настроек.
ALTER TABLE user_settings
    ADD COLUMN replacement_horizon_years DECIMAL(4,2) NOT NULL DEFAULT 2.00 AFTER commission_rate_override;
