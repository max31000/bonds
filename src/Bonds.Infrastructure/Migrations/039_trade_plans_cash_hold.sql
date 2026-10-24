-- Задача 67 (аудит #3): удержание денег под вывод.
-- trade_plans.cash_hold_released_at - когда владелец отметил «деньги выведены» (команда /withdrawn в боте или кнопка на странице «Планы»).
--   NULL - удержание плана вывода (summary_json.cashHold) действует, пока план открыт или не прошло Policy:WithdrawHoldTtl рабочих дней с его исполнения
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

ALTER TABLE trade_plans ADD COLUMN cash_hold_released_at DATETIME NULL
