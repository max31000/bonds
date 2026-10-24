-- T-67E (minor 7): вторая половина замены внешнего ключа trade_orders.plan_id -> trade_plans.id (первая - 040, DROP старого CASCADE).
-- ON DELETE RESTRICT: план с заявками удалить нельзя. Новое имя ключа - чтобы миграция не падала на базе с прежней 040 (там уже есть fk_trade_orders_plan RESTRICT).
-- Справка (комментарии уже выкаченных миграций не правятся): в 037 сказано «user_order_id (10 цифр)» - на деле 9 цифр, диапазон 700000000-799999999
--   (FreedomOrderRules.MinUserOrderId / MaxUserOrderIdExclusive).
-- Примечание: в комментариях миграций не использовать символ точка-с-запятой (MigrationRunner делит файл по нему).

ALTER TABLE trade_orders ADD CONSTRAINT fk_trade_orders_plan_restrict FOREIGN KEY (plan_id) REFERENCES trade_plans (id) ON DELETE RESTRICT
