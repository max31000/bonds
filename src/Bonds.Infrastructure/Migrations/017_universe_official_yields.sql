-- Задача 42: биржевые метрики в банке облигаций (блок ISS marketdata_yields + НКД/объём выпуска из
-- securities) и расширенная дневная история (bid/offer/листинг/эффективный спред).
-- Единицы (контракт docs/CODEBASE-GUIDE.md): *_fraction - ДОЛИ (0.0035 = 35 б.п. = 0.35 п.п.),
-- MOEX отдаёт EFFECTIVEYIELD/YIELDTOOFFER в процентах (делим на 100), GSPREADBP/ZSPREADBP в
-- базисных пунктах (делим на 10000). accrued_rub - РУБЛИ на одну бумагу (MOEX ACCRUEDINT, без
-- конверсии). bid/offer в истории - % от номинала как у bid_percent/offer_percent в bond_universe.
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner
-- делит файл по этому символу без учёта SQL-комментариев).

ALTER TABLE bond_universe
    ADD COLUMN accrued_rub               DECIMAL(18,6) NULL AFTER gspread_approx_fraction,
    ADD COLUMN issue_size                BIGINT        NULL AFTER accrued_rub,
    ADD COLUMN effective_yield_fraction  DECIMAL(12,8) NULL AFTER issue_size,
    ADD COLUMN yield_to_offer_fraction   DECIMAL(12,8) NULL AFTER effective_yield_fraction,
    ADD COLUMN yield_date_type           VARCHAR(16)   NULL AFTER yield_to_offer_fraction,
    ADD COLUMN gspread_official_fraction DECIMAL(12,8) NULL AFTER yield_date_type,
    ADD COLUMN zspread_official_fraction DECIMAL(12,8) NULL AFTER gspread_official_fraction;

-- gspread_effective_fraction = COALESCE(gspread_official_fraction, gspread_approx_fraction) на момент
-- среза (BondUniverseEntry.EffectiveGspreadFraction). У строк, записанных до этой миграции, NULL -
-- читатели берут gspread_approx_fraction.
ALTER TABLE bond_universe_history
    ADD COLUMN bid                        DECIMAL(12,6)    NULL AFTER price_percent,
    ADD COLUMN offer                      DECIMAL(12,6)    NULL AFTER bid,
    ADD COLUMN list_level                 TINYINT UNSIGNED NULL AFTER offer,
    ADD COLUMN gspread_effective_fraction DECIMAL(12,8)    NULL AFTER list_level;
