-- Задача 19 (часть A): дневная история цен инструмента для графика цены карточки позиции
-- (GET /api/positions/{id}?range=...). Кэш поверх MoexIssClient.GetHistoryPricesAsync (задача 15
-- §A) — один снимок на (инструмент, дата), апсертится, дозагружается только недостающий хвост
-- (см. doc-comment InstrumentPriceHistoryService), не вся история при каждом запросе.
CREATE TABLE IF NOT EXISTS instrument_price_history (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    instrument_id       BIGINT UNSIGNED NOT NULL,
    date                DATE            NOT NULL,
    close_price_percent DECIMAL(18,6)   NULL,
    accrued_interest_rub DECIMAL(18,6)  NULL,
    created_at          DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    UNIQUE KEY uq_instrument_price_history_instrument_date (instrument_id, date),
    KEY idx_instrument_price_history_instrument (instrument_id),
    CONSTRAINT fk_instrument_price_history_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
