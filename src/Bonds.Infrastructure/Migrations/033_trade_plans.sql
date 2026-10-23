-- Задача 63: планы сделок (движок правил, dry-run, БЕЗ исполнения - заявки брокерам не отправляются).
-- trade_plans - план одного правила:
--   rule        - P1_SWEEP, P2_INFLOW, P3B_LIQ_TO_BONDS, P4_REBALANCE, P5_WITHDRAW, P7_FREEDOM_TOPUP или MANUAL
--   trigger     - причина человеческим языком (для правил по расписанию стабильна - по ней нет дубликатов открытого плана)
--   status      - Draft, PendingApproval, AutoApproved, Approved, Rejected, Expired, Submitting, Done или Failed
--   dry_run     - 1 = не исполнять (на этой фазе всегда 1)
--   decided_via - web, telegram или auto
--   summary_json- итоги плана (деньги до и после, комиссия, налог, предупреждения), все суммы в валюте плана
-- trade_plan_lines - строки плана (одна строка = одна будущая лимитная заявка):
--   quantity_lots - ЦЕЛЫЕ лоты, lot_size - штук в лоте на момент плана (штуки = лоты на размер лота)
--   limit_price   - цена за 1 штуку в валюте цены (для облигаций - рубли чистой цены за штуку, при нерублёвом номинале оценка)
--   limit_price_points - только облигации: цена в ПУНКТАХ (процент номинала), по сетке min_price_increment_points - её и использует исполнитель
--   est_amount / est_fee / est_tax - неотрицательные оценки в валюте строки (сумма сделки с НКД, комиссия, налог при продаже)
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

CREATE TABLE IF NOT EXISTS trade_plans (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id       BIGINT UNSIGNED NOT NULL,
    rule          VARCHAR(24)     NOT NULL,
    `trigger`     VARCHAR(255)    NOT NULL,
    status        VARCHAR(16)     NOT NULL,
    dry_run       TINYINT(1)      NOT NULL DEFAULT 1,
    created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at    DATETIME        NOT NULL,
    decided_at    DATETIME        NULL,
    decided_via   VARCHAR(16)     NULL,
    summary_json  JSON            NULL,

    PRIMARY KEY (id),
    KEY idx_trade_plans_user_status (user_id, status, created_at),
    KEY idx_trade_plans_user_rule (user_id, rule, `trigger`),
    CONSTRAINT fk_trade_plans_user FOREIGN KEY (user_id)
        REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS trade_plan_lines (
    plan_id            BIGINT UNSIGNED NOT NULL,
    line_no            INT             NOT NULL,
    broker             VARCHAR(16)     NOT NULL,
    account_id         BIGINT UNSIGNED NOT NULL,
    instrument_key     VARCHAR(64)     NOT NULL,
    ticker             VARCHAR(32)     NOT NULL,
    side               VARCHAR(4)      NOT NULL,
    quantity_lots      BIGINT          NOT NULL,
    lot_size           DECIMAL(20,6)   NOT NULL DEFAULT 1,
    limit_price        DECIMAL(24,8)   NOT NULL,
    limit_price_points DECIMAL(24,8)   NULL,
    est_amount         DECIMAL(24,4)   NOT NULL,
    est_fee            DECIMAL(24,4)   NOT NULL DEFAULT 0,
    est_tax            DECIMAL(24,4)   NOT NULL DEFAULT 0,
    currency           VARCHAR(8)      NOT NULL,
    reason             VARCHAR(500)    NOT NULL DEFAULT '',

    PRIMARY KEY (plan_id, line_no),
    CONSTRAINT fk_trade_plan_lines_plan FOREIGN KEY (plan_id)
        REFERENCES trade_plans (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
