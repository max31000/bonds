-- Задача 16 (часть A): лёгкий контур "только цены" — тики котировок открытых позиций, собираемые
-- LiveQuotesPollingService раз в PollingInterval в торговые часы MOEX. Отдельная таблица от
-- market_quotes (которая хранит один срез в день на инструмент из полного синка) — здесь плотный
-- временной ряд intraday, retention 8 дней (см. LiveQuotesPollingService.WriteTicksAsync).
CREATE TABLE IF NOT EXISTS intraday_quotes (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    instrument_id   BIGINT UNSIGNED NOT NULL,
    ts_utc          DATETIME(3)     NOT NULL,
    dirty_price_rub DECIMAL(18,6)   NOT NULL,
    created_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (id),
    KEY idx_intraday_quotes_instrument_ts (instrument_id, ts_utc),
    KEY idx_intraday_quotes_ts (ts_utc),
    CONSTRAINT fk_intraday_quotes_instrument FOREIGN KEY (instrument_id)
        REFERENCES instruments (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
