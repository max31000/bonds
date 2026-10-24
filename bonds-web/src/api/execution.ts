import { apiClient } from './client';
import type { ExecutionState, Plan } from './types';

/** GET /api/settings/execution — режим пользователя и все условия ворот исполнения (задача 65). */
export function fetchExecutionState(): Promise<ExecutionState> {
  return apiClient.get<ExecutionState>('/settings/execution');
}

/**
 * PUT /api/settings/execution-mode. `live` требует проверенный торговый токен и `confirmation` ровно «LIVE» (422 иначе);
 * включение отправляет в бот «Исполнение включено». Глобальный выключатель сервера отсюда не меняется.
 */
export function setExecutionMode(mode: 'observe' | 'live', confirmation?: string): Promise<ExecutionState> {
  return apiClient.put<ExecutionState>('/settings/execution-mode', { mode, confirmation });
}

/** PUT /api/settings/execution-pause — то же, что `/stop` / `/resume` в боте («Остановить»). */
export function setExecutionPaused(paused: boolean): Promise<ExecutionState> {
  return apiClient.put<ExecutionState>('/settings/execution-pause', { paused });
}

/** POST /api/plans/{id}/cancel-execution — снять висящие заявки плана (флаг для исполнителя). 409 — план не исполняется. */
export function cancelPlanExecution(id: number): Promise<Plan> {
  return apiClient.post<Plan>(`/plans/${id}/cancel-execution`);
}
