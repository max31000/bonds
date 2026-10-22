import { apiClient } from './client';
import type { CurrenciesResponse } from './types';

/** GET /api/currencies — валюта на счёте (юани и т.п.): курс, стоимость, курсовая разница, история курса. Включает проданную (qty 0). */
export function fetchCurrencies(): Promise<CurrenciesResponse> {
  return apiClient.get<CurrenciesResponse>('/currencies');
}
