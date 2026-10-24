import { apiClient } from './client';
import type { Plan, PlanInflowBody, PlanResult, PlansResponse, PlanStatus, PlanWithdrawBody } from './types';

/** GET /api/plans[?status] — планы сделок пользователя (новые сверху). Режим наблюдения: заявки брокерам не отправляются. */
export function fetchPlans(status?: PlanStatus): Promise<PlansResponse> {
  return apiClient.get<PlansResponse>(`/plans${status ? `?status=${status}` : ''}`);
}

/** POST /api/plans/{id}/approve — в режиме наблюдения план получает статус Approved с пометкой «не исполнен: dry-run». 409 — план уже решён или просрочен. */
export function approvePlan(id: number): Promise<Plan> {
  return apiClient.post<Plan>(`/plans/${id}/approve`);
}

/** POST /api/plans/{id}/reject. */
export function rejectPlan(id: number): Promise<Plan> {
  return apiClient.post<Plan>(`/plans/${id}/reject`);
}

/** POST /api/plans/inflow — распределить сумму (валюта портфеля) по корзинам по дефициту. `plan: null` — плана нет, причины в `notes`. */
export function createInflowPlan(body: PlanInflowBody): Promise<PlanResult> {
  return apiClient.post<PlanResult>('/plans/inflow', body);
}

/** POST /api/plans/withdraw — вывод: ровно одно из `amount` (после налога и комиссии) и `fraction` (ДОЛЯ портфеля). */
export function createWithdrawPlan(body: PlanWithdrawBody): Promise<PlanResult> {
  return apiClient.post<PlanResult>('/plans/withdraw', body);
}

/** POST /api/plans/exit-basket — продажа всех членов корзины без цели (временной). */
export function createExitBasketPlan(code: string): Promise<PlanResult> {
  return apiClient.post<PlanResult>('/plans/exit-basket', { code });
}

/** POST /api/plans/cash-hold/release — «Деньги выведены» (задача 67, то же, что /withdrawn в боте): снять удержание денег под вывод у всех планов вывода. */
export function releaseCashHold(): Promise<{ released: number }> {
  return apiClient.post<{ released: number }>('/plans/cash-hold/release');
}
