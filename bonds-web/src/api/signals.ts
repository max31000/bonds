import { apiClient } from './client';
import type { SignalReadResponse, SignalsResponse } from './types';

/** GET /api/signals — список сигналов по позициям/портфелю (plan/09c §B.6). */
export function fetchSignals(params?: { unreadOnly?: boolean }): Promise<SignalsResponse> {
  const query = new URLSearchParams();
  if (params?.unreadOnly) query.set('unreadOnly', 'true');
  const qs = query.toString();
  return apiClient.get<SignalsResponse>(`/signals${qs ? `?${qs}` : ''}`);
}

/** POST /api/signals/{id}/read — отметить сигнал прочитанным. */
export function markSignalRead(id: number): Promise<SignalReadResponse> {
  return apiClient.post<SignalReadResponse>(`/signals/${id}/read`);
}
