-- Задача 46: данные для скоркарты надёжности.
-- instrument_risk_levels: брокерская оценка риска облигации из T-Invest (Bond.risk_level, instruments.proto:502).
-- Один bulk-вызов Instruments.Bonds() раз в сутки (шаг цикла синка) - таблица отдельна от instruments,
-- чтобы покрывать весь банк облигаций, а не только бумаги портфеля. Ключ - ISIN-строка (без FK, как watchlist).
-- risk_level: 0 = не определён, 1 = низкий, 2 = умеренный, 3 = высокий (числа протобуф-enum).
-- fetched_at - UTC.
-- issuer_ratings: рейтинги эмитентов, введённые владельцем вручную (бесплатного источника нет).
-- Ключ - (inn, agency): у каждого агентства одна актуальная запись, rating_date - дата присвоения
-- или подтверждения (рейтинг старше 18 месяцев в скоркарте считается протухшим).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner
-- делит файл по этому символу без учёта SQL-комментариев).

CREATE TABLE IF NOT EXISTS instrument_risk_levels (
    isin        VARCHAR(12) NOT NULL,
    risk_level  TINYINT     NOT NULL,
    fetched_at  DATETIME    NOT NULL,

    PRIMARY KEY (isin)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS issuer_ratings (
    inn          VARCHAR(12)  NOT NULL,
    agency       VARCHAR(32)  NOT NULL,
    rating       VARCHAR(16)  NOT NULL,
    rating_date  DATE         NOT NULL,
    note         VARCHAR(512) NULL,

    UNIQUE KEY uq_issuer_ratings (inn, agency)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
