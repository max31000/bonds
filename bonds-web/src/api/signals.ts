import { apiClient } from './client';
import type { SignalReadResponse, SignalsReadAllResponse, SignalsResponse } from './types';

/**
 * GET /api/signals — АКТИВНЫЕ события (по умолчанию) либо, с `includeResolved`, вместе с закрытыми
 * (история). Маршрут исторически /signals; в UI это «События».
 */
export function fetchSignals(params?: { includeResolved?: boolean }): Promise<SignalsResponse> {
  const query = new URLSearchParams();
  if (params?.includeResolved) query.set('include', 'resolved');
  const qs = query.toString();
  return apiClient.get<SignalsResponse>(`/signals${qs ? `?${qs}` : ''}`);
}

/** POST /api/signals/{id}/read — подтвердить («прочитано») событие. */
export function markSignalRead(id: number): Promise<SignalReadResponse> {
  return apiClient.post<SignalReadResponse>(`/signals/${id}/read`);
}

/** POST /api/signals/read-all — подтвердить все активные события. */
export function markAllSignalsRead(): Promise<SignalsReadAllResponse> {
  return apiClient.post<SignalsReadAllResponse>('/signals/read-all');
}
