import type { Plan, PlanLine, PlanLineExecutionStatus, PlanOrder, PlanRule, PlanStatus } from '../api/types';
import { formatMoney } from './format';

/**
 * Планы сделок (задача 63). Суммы приходят в валюте строки/портфеля, доли — только `fraction` запроса вывода (ДОЛЯ), проценты рисует
 * лишь форма (через `percentToFraction` из `utils/baskets.ts`). Здесь — только подписи и форматирование, расчётов нет.
 */

export const PLAN_RULE_LABEL: Record<PlanRule, string> = {
  P1_SWEEP: 'Парковка свободных денег',
  P2_INFLOW: 'Распределение пополнения',
  P3B_LIQ_TO_BONDS: 'Перелив ликвидности в облигации',
  P4_REBALANCE: 'Ребаланс корзин',
  P5_WITHDRAW: 'Вывод средств',
  P7_FREEDOM_TOPUP: 'Доливка Freedom',
  MANUAL: 'Ручной план',
};

export const PLAN_STATUS_LABEL: Record<PlanStatus, string> = {
  Draft: 'Черновик',
  PendingApproval: 'Ждёт решения',
  AutoApproved: 'Авто-подтверждён',
  Approved: 'Подтверждён',
  Rejected: 'Отклонён',
  Expired: 'Истёк',
  Submitting: 'Исполняется',
  Done: 'Исполнен',
  Failed: 'Не исполнен',
  PartiallyDone: 'Исполнен частично',
};

export const PLAN_STATUS_COLOR: Record<PlanStatus, string> = {
  Draft: 'gray',
  PendingApproval: 'yellow',
  AutoApproved: 'teal',
  Approved: 'green',
  Rejected: 'gray',
  Expired: 'gray',
  Submitting: 'blue',
  Done: 'green',
  Failed: 'red',
  PartiallyDone: 'orange',
};

/** Статус строки/заявки исполнителя для человека (задача 65). */
export const LINE_EXECUTION_LABEL: Record<PlanLineExecutionStatus, string> = {
  Pending: 'Не отправлена',
  New: 'Отправляется',
  Submitted: 'Выставлена',
  PartiallyFilled: 'Исполнена частично',
  Filled: 'Исполнена',
  Cancelled: 'Снята',
  Rejected: 'Отклонена брокером',
  Failed: 'Не отправлена (проверка)',
  Expired: 'Не исполнена (попытки исчерпаны)',
};

export const LINE_EXECUTION_COLOR: Record<PlanLineExecutionStatus, string> = {
  Pending: 'gray',
  New: 'blue',
  Submitted: 'blue',
  PartiallyFilled: 'orange',
  Filled: 'green',
  Cancelled: 'gray',
  Rejected: 'red',
  Failed: 'red',
  Expired: 'gray',
};

/** Можно попросить снять висящие заявки (план исполняется или ждёт исполнителя). */
export function isCancellableExecution(plan: Pick<Plan, 'status' | 'dryRun'>): boolean {
  return plan.status === 'Submitting' || (!plan.dryRun && (plan.status === 'Approved' || plan.status === 'AutoApproved'));
}

/** Планы, по которым ещё можно принять решение. */
export function isDecidable(status: PlanStatus): boolean {
  return status === 'PendingApproval' || status === 'Draft';
}

/** Сумма в валюте плана: рубли — «1 234,56 RUB» с кодом (знак ₽ был бы ложью для USD-планов), 2 знака. */
export function formatPlanMoney(value: number | null | undefined, currency: string): string {
  return formatMoney(value, currency, 2);
}

/** «26 лот. (26 шт.)» — целые лоты и штуки (если лот не 1). */
export function formatQuantity(line: Pick<PlanLine, 'quantityLots' | 'lotSize' | 'quantityPieces'>): string {
  const lots = `${line.quantityLots} лот.`;
  return line.lotSize === 1 ? lots : `${lots} (${line.quantityPieces} шт.)`;
}

/** Цена заявки исполнителя: облигации — в пунктах («92,7 п.»), остальное — в валюте цены за штуку. */
export function formatOrderPrice(order: Pick<PlanOrder, 'limitPrice' | 'priceUnit' | 'currency'>): string {
  if (order.priceUnit === 'points') {
    return `${new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(order.limitPrice)} п.`;
  }
  return formatMoney(order.limitPrice, order.currency, 2);
}

/** Лимитная цена: облигации — «99,95 п. (999,50 RUB)», остальные — цена за штуку в валюте цены. */
export function formatLimitPrice(line: Pick<PlanLine, 'limitPrice' | 'limitPricePoints' | 'currency'>): string {
  const money = formatMoney(line.limitPrice, line.currency, 2);
  if (line.limitPricePoints === null || line.limitPricePoints === undefined) return money;
  const points = new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(line.limitPricePoints);
  return `${points} п. (${money})`;
}
