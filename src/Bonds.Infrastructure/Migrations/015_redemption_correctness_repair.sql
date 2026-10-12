-- plan/39: погашения и «редкие» события.
-- 1) Переводы бумаг между счетами (T-Invest InputSecurities/OutputSecurities) — новые типы операций.
ALTER TABLE operations
    MODIFY COLUMN type ENUM('Buy','Sell','Coupon','Amortization','Redemption','Tax','Fee','SecurityIn','SecurityOut') NOT NULL;

-- 2) Ремонт данных. Операции купли/продажи/выплат БЕЗ инструмента — это либо акции/фонды/валюта
-- (раньше попадали в журнал и портили XIRR), либо облигации, чей FIGI не удалось сопоставить.
-- Синк вычитывает журнал брокера целиком при каждом запуске, поэтому корректные записи вернутся
-- на следующем синке (уже с резолвом инструмента и фильтром «только облигации»).
DELETE FROM operations
WHERE instrument_id IS NULL
  AND type IN ('Buy','Sell','Coupon','Amortization','Redemption','SecurityIn','SecurityOut');

-- 3) Сохранённые снимки стоимости содержат «призраки» погашенных бумаг (ОФЗ 26226 и т.п.) и
-- неверный XIRR. Очистка: при старте приложения стартовый бэкфилл (SyncSchedulerHostedService,
-- порог < 5 снимков при непустом журнале) пересоберёт всю историю с уже исправленной логикой.
DELETE FROM portfolio_value_snapshots;
