-- Задача 20 (часть A): ручной watchlist — чужие бумаги вне текущих позиций, отслеживаемые по ISIN
-- (не скринер по всей вселенной, см. doc-comment ICandidateScreener.cs). Watchlist-бумаги заводятся
-- в общий справочник instruments тем же путём, что и позиции (BondSyncService.EnrichFromMoexAsync) —
-- эта таблица хранит только сам факт "пользователь отслеживает ISIN X" + заметку, без дублирования
-- справочных/расчётных данных.
CREATE TABLE IF NOT EXISTS watchlist_items (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id       BIGINT UNSIGNED NOT NULL,
    isin          VARCHAR(12)     NOT NULL,
    added_at_utc  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    note          VARCHAR(500)    NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_watchlist_items_user_isin (user_id, isin),
    KEY idx_watchlist_items_user (user_id),
    CONSTRAINT fk_watchlist_items_user FOREIGN KEY (user_id)
        REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
