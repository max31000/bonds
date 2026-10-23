import { apiClient } from './client';
import type { BrokerHoldingsResponse, BrokerOperationsResponse } from './types';

/** GET /api/brokers/freedom/holdings — позиции и деньги счёта Freedom (в валюте строки + рубли по курсам bonds). 404 — счёт не подключён. */
export function fetchFreedomHoldings(): Promise<BrokerHoldingsResponse> {
  return apiClient.get<BrokerHoldingsResponse>('/brokers/freedom/holdings');
}

/** GET /api/brokers/freedom/operations — журнал Freedom (новые сверху), даты `yyyy-MM-dd` включительно. */
export function fetchFreedomOperations(range: { from?: string; to?: string } = {}): Promise<BrokerOperationsResponse> {
  const q = new URLSearchParams();
  if (range.from) q.set('from', range.from);
  if (range.to) q.set('to', range.to);
  const qs = q.toString();
  return apiClient.get<BrokerOperationsResponse>(`/brokers/freedom/operations${qs ? `?${qs}` : ''}`);
}
