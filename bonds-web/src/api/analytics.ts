import { apiClient } from './client';
import type { CompositionResponse, RateScenarioResponse, ScatterResponse, XirrResponse } from './types';

/** GET /api/analytics/scatter — точки «дюрация × доходность» + безрисковая кривая (plan/09b §B.3). */
export function fetchScatter(): Promise<ScatterResponse> {
  return apiClient.get<ScatterResponse>('/analytics/scatter');
}

/** GET /api/analytics/composition — композиция портфеля по 4 разрезам (plan/09b §B.4). */
export function fetchComposition(): Promise<CompositionResponse> {
  return apiClient.get<CompositionResponse>('/analytics/composition');
}

/** GET /api/analytics/xirr — текущий XIRR и история во времени (plan/09b §B.5). */
export function fetchXirr(params?: { from?: string; to?: string }): Promise<XirrResponse> {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  const qs = query.toString();
  return apiClient.get<XirrResponse>(`/analytics/xirr${qs ? `?${qs}` : ''}`);
}

/** GET /api/analytics/rate-scenario — портфель при параллельном сдвиге ставок. */
export function fetchRateScenario(): Promise<RateScenarioResponse> {
  return apiClient.get<RateScenarioResponse>('/analytics/rate-scenario');
}
