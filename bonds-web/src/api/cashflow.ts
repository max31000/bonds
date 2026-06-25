import { apiClient } from './client';
import type { CashflowResponse } from './types';

/** GET /api/cashflow — календарь поступлений по месяцам/позициям (см. plan/08, plan/09b §B.2). */
export function fetchCashflow(params?: { from?: string; to?: string }): Promise<CashflowResponse> {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  const qs = query.toString();
  return apiClient.get<CashflowResponse>(`/cashflow${qs ? `?${qs}` : ''}`);
}
