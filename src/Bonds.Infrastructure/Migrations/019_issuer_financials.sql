-- Задача 45: отчётность эмитентов (ГИР БО ФНС, bo.nalog.gov.ru) для кредитного слоя скоркарты.
-- Единицы (контракт docs/CODEBASE-GUIDE.md): ВСЕ денежные колонки issuer_financials - РУБЛИ.
-- ГИР БО отдаёт тысячи рублей, конверсия x1000 делается один раз на границе парсинга
-- (GirBoParser.ThousandsToRubles), в БД тысяч нет. NULL в колонке - строка формы не заполнена, не ноль.
-- fetched_at / checked_at - UTC. period - отчётный год. has_audit известен только для отчётного года
-- подачи (для предыдущих лет той же подачи NULL).
-- Связь с инструментами - по ИНН-строке (bond_universe.emitent_inn), без FK, как watchlist по ISIN.
-- source: 'girbo' (автозагрузка) или 'manual' (ручной ввод) - ключ включает source, поэтому
-- автозагрузка не затирает ручные правки.
-- issuer_financials_status хранит итог последней проверки ИНН, чтобы не долбить ГИР БО по эмитентам,
-- которых там нет: ok, not_found, hidden (организация есть, подач нет), cb (кредитная организация), error.
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner
-- делит файл по этому символу без учёта SQL-комментариев).

CREATE TABLE IF NOT EXISTS issuer_financials (
    inn                  VARCHAR(12)   NOT NULL,
    period               SMALLINT      NOT NULL,
    source               VARCHAR(16)   NOT NULL,
    fetched_at           DATETIME      NOT NULL,

    revenue              DECIMAL(20,2) NULL,
    operating_profit     DECIMAL(20,2) NULL,
    interest_paid        DECIMAL(20,2) NULL,
    net_profit           DECIMAL(20,2) NULL,
    equity               DECIMAL(20,2) NULL,
    total_assets         DECIMAL(20,2) NULL,
    current_assets       DECIMAL(20,2) NULL,
    current_liabilities  DECIMAL(20,2) NULL,
    cash                 DECIMAL(20,2) NULL,
    long_debt            DECIMAL(20,2) NULL,
    short_debt           DECIMAL(20,2) NULL,
    retained_earnings    DECIMAL(20,2) NULL,
    has_audit            TINYINT(1)    NULL,

    UNIQUE KEY uq_issuer_financials (inn, period, source)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS issuer_financials_status (
    inn         VARCHAR(12) NOT NULL,
    status      VARCHAR(16) NOT NULL,
    checked_at  DATETIME    NOT NULL,

    PRIMARY KEY (inn),
    KEY idx_issuer_financials_status_checked (checked_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
