-- Задача 65: исполнитель планов T-Invest (ВЫКЛЮЧЕН по умолчанию: env Execution__Enabled=false, режим пользователя observe, нужен проверенный торговый токен).
-- user_settings.execution_mode - observe (по умолчанию, заявки не отправляются) или live
-- trade_plans.cancel_requested - 1 = пользователь попросил снять висящие заявки плана (кнопка Отменить в боте или вебе), исполнитель снимает их на ближайшем тике
-- trade_plans.status - добавлен PartiallyDone (часть строк исполнена), колонка VARCHAR - схема не меняется
-- trade_orders - заявки исполнителя, одна строка = одна попытка (attempt) отправки строки плана:
--   client_order_id - UUID, создаётся и ЗАПИСЫВАЕТСЯ ДО отправки, уходит брокеру как order_id (идемпотентность на стороне брокера), UNIQUE
--   broker_order_id - id заявки у брокера (после ответа PostOrder или сверки GetOrderState)
--   status          - New, Submitted, PartiallyFilled, Filled, Cancelled, Rejected, Failed или Expired
--   limit_price     - цена заявки В ЕДИНИЦАХ БРОКЕРА: облигации - ПУНКТЫ (процент номинала), остальное - валюта цены за 1 штуку
--   price_unit      - points или currency (как limit_price передан брокеру)
--   lots_requested / lots_filled - ЦЕЛЫЕ лоты
--   avg_fill_price  - средняя цена исполнения за 1 штуку в валюте (MoneyValue брокера, для облигаций - рубли с учётом номинала, без НКД)
--   executed_commission - фактическая комиссия брокера в currency
--   error_code / error_message - причина отказа без секретов
-- user_credentials tinvest_trade - до задачи 65 проверка была только формальной: такие записи переводятся в unverified (нужна сетевая проверка заново)
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

ALTER TABLE user_settings ADD COLUMN execution_mode VARCHAR(8) NOT NULL DEFAULT 'observe';

ALTER TABLE trade_plans ADD COLUMN cancel_requested TINYINT(1) NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS trade_orders (
    id                   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    plan_id              BIGINT UNSIGNED NOT NULL,
    line_no              INT             NOT NULL,
    attempt              INT             NOT NULL,
    client_order_id      VARCHAR(36)     NOT NULL,
    broker_order_id      VARCHAR(64)     NULL,
    status               VARCHAR(16)     NOT NULL,
    limit_price          DECIMAL(24,9)   NOT NULL,
    price_unit           VARCHAR(8)      NOT NULL,
    lots_requested       BIGINT          NOT NULL,
    lots_filled          BIGINT          NOT NULL DEFAULT 0,
    avg_fill_price       DECIMAL(24,9)   NULL,
    executed_commission  DECIMAL(24,9)   NULL,
    currency             VARCHAR(8)      NOT NULL,
    error_code           VARCHAR(64)     NULL,
    error_message        VARCHAR(500)    NULL,
    created_at           DATETIME        NOT NULL,
    updated_at           DATETIME        NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_trade_orders_client_order_id (client_order_id),
    UNIQUE KEY uq_trade_orders_line_attempt (plan_id, line_no, attempt),
    KEY idx_trade_orders_status (status),
    CONSTRAINT fk_trade_orders_plan FOREIGN KEY (plan_id)
        REFERENCES trade_plans (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

UPDATE user_credentials
SET status = 'unverified', status_message = 'Нужна повторная проверка торгового токена (задача 65: проверка прав и счёта у брокера)'
WHERE provider = 'tinvest_trade' AND status = 'ok';
