import { apiClient } from './client';
import type { CashOverviewResponse } from './types';

/** GET /api/cash — деньги по брокерам и валютам (рубли без валюты — `cashRubOnly`) и последние пополнения/выводы. */
export function fetchCash(): Promise<CashOverviewResponse> {
  return apiClient.get<CashOverviewResponse>('/cash');
}
