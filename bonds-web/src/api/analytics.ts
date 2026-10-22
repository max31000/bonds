import { apiClient } from './client';
import type { CompositionResponse, RateScenarioResponse, ScatterResponse, TrajectoryResponse, XirrBackfillResponse, XirrResponse } from './types';

/** GET /api/analytics/scatter — точки «дюрация × доходность» + безрисковая кривая. */
export function fetchScatter(): Promise<ScatterResponse> {
  return apiClient.get<ScatterResponse>('/analytics/scatter');
}

/**
 * Query-параметр `includeFunds` (задача 57): бэкенд по умолчанию считает «облигации + фонды»; `false` — прежняя облигационная картина.
 * Дефолт не шлём (одно место правды — бэкенд), в запрос уходит только явное `includeFunds=false`.
 */
function fundsQuery(includeFunds: boolean | undefined): string {
  return includeFunds === false ? '?includeFunds=false' : '';
}

/**
 * GET /api/analytics/composition — композиция портфеля по 5 разрезам (эмитент/сектор/купон/дюрация/класс актива).
 * `includeFunds: false` — только облигации (нужно там, где важна доля эмитента среди облигаций, напр. лимит концентрации).
 */
export function fetchComposition(params?: { includeFunds?: boolean }): Promise<CompositionResponse> {
  return apiClient.get<CompositionResponse>(`/analytics/composition${fundsQuery(params?.includeFunds)}`);
}

/** GET /api/analytics/xirr — текущий XIRR и история во времени. */
export function fetchXirr(params?: { from?: string; to?: string }): Promise<XirrResponse> {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  const qs = query.toString();
  return apiClient.get<XirrResponse>(`/analytics/xirr${qs ? `?${qs}` : ''}`);
}

/** POST /api/analytics/xirr/backfill — ретроспективный бэкфилл истории XIRR из MOEX ISS. */
export function postXirrBackfill(): Promise<XirrBackfillResponse> {
  return apiClient.post<XirrBackfillResponse>('/analytics/xirr/backfill');
}

/** GET /api/analytics/rate-scenario — портфель при параллельном сдвиге ставок (по умолчанию с фондами, `includeFunds: false` — только облигации). */
export function fetchRateScenario(params?: { includeFunds?: boolean }): Promise<RateScenarioResponse> {
  return apiClient.get<RateScenarioResponse>(`/analytics/rate-scenario${fundsQuery(params?.includeFunds)}`);
}

/** GET /api/analytics/trajectory — траектория стоимости портфеля во времени. */
export function fetchTrajectory(params?: { horizonMonths?: number; reinvestRate?: number }): Promise<TrajectoryResponse> {
  const query = new URLSearchParams();
  if (params?.horizonMonths) query.set('horizonMonths', String(params.horizonMonths));
  if (params?.reinvestRate !== undefined) query.set('reinvestRate', String(params.reinvestRate));
  const qs = query.toString();
  return apiClient.get<TrajectoryResponse>(`/analytics/trajectory${qs ? `?${qs}` : ''}`);
}
