-- Задача 49: рыночные и кредитные события портфеля (SpreadWidening, PriceDrawdown, IssuerDefaultEvent).
-- 1) Расширяем ENUM signals.type тремя новыми типами (существующие строки не затрагиваются).
-- 2) Пороги новых правил - настройки пользователя (NULL = дефолт SignalEngineOptions).
-- Единицы (контракт docs/CODEBASE-GUIDE.md): оба порога - ДОЛИ (0.0050 = 50 б.п. G-спреда, 0.05 = просадка цены 5%).
-- Диапазоны валидируются на границе API (SettingsEndpoints).
ALTER TABLE signals
    MODIFY COLUMN type ENUM('UpcomingAmortization','UpcomingRedemption','UpcomingOffer',
                            'FloaterRateReset','UninvestedCashThreshold','ConcentrationLimitBreached',
                            'SpreadWidening','PriceDrawdown','IssuerDefaultEvent') NOT NULL;

ALTER TABLE user_settings
    ADD COLUMN spread_widening_threshold_fraction DECIMAL(8,6) NULL AFTER replacement_horizon_years,
    ADD COLUMN price_drawdown_threshold_fraction  DECIMAL(8,6) NULL AFTER spread_widening_threshold_fraction;
