-- Plan/22 часть D: ручной override ставки комиссии брокера (доля, не процент) - страховка на
-- случай, когда авто-оценка из журнала операций (часть A) недоступна или пользователь знает
-- точную ставку своего тарифа лучше, чем оценка по историческим Fee-операциям.
ALTER TABLE user_settings
    ADD COLUMN commission_rate_override DECIMAL(8,6) NULL AFTER duration_drift_tolerance_years;
