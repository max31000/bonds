-- Свободные деньги и полная стоимость счёта по оценке брокера (T-Invest GetPortfolio:
-- TotalAmountCurrencies / TotalAmountPortfolio, РУБЛИ). Нужны, чтобы итог в сервисе сходился с
-- приложением брокера: после погашения/купона деньги лежат на счёте, а не в облигациях.
-- NULL — синк с брокером ещё не отдавал баланс.
ALTER TABLE accounts
    ADD COLUMN cash_rub          DECIMAL(18,6) NULL AFTER name,
    ADD COLUMN broker_total_rub  DECIMAL(18,6) NULL AFTER cash_rub,
    ADD COLUMN balance_synced_at DATETIME      NULL AFTER broker_total_rub;
