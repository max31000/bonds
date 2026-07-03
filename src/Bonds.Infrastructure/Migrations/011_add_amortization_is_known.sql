-- Audit(engine) E-1: амортизация с известной датой, но неизвестной суммой (MOEX value_rub=null,
-- реалистично для ипотечных агентов/MBS-подобных бумаг) больше не выбрасывается парсером молча -
-- зеркалим coupon_schedules.is_known. amount_rub для таких строк - 0m-заглушка, не реальный ноль,
-- см. AmortizationSchedule.AmountRub doc-comment.
ALTER TABLE amortization_schedules
    ADD COLUMN is_known TINYINT(1) NOT NULL DEFAULT 1 AFTER amount_rub;
