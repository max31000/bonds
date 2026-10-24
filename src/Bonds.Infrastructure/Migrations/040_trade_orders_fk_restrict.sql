-- Задача 67 (NIT): заявки брокеру - журнал денег, удаление плана не должно молча стирать его заявки.
-- trade_orders.plan_id -> trade_plans.id: ON DELETE CASCADE заменяется на ON DELETE RESTRICT (план с заявками удалить нельзя, сначала разбор заявок).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

ALTER TABLE trade_orders DROP FOREIGN KEY fk_trade_orders_plan;

ALTER TABLE trade_orders ADD CONSTRAINT fk_trade_orders_plan FOREIGN KEY (plan_id) REFERENCES trade_plans (id) ON DELETE RESTRICT
