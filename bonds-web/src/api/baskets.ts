import { apiClient } from './client';
import type { BasketBatchItem, BasketsResponse, CapitalPortfolioId } from './types';

/** GET /api/baskets — корзины, члены и отклонения по двум портфелям (первый вызов сидит стартовые корзины). */
export function fetchBaskets(): Promise<BasketsResponse> {
  return apiClient.get<BasketsResponse>('/baskets');
}

/** PUT /api/baskets — цели нескольких корзин атомарно; сумма целей портфеля проверяется на итоговом состоянии (422 — `error` с текстом). */
export function updateBaskets(items: BasketBatchItem[]): Promise<BasketsResponse> {
  return apiClient.put<BasketsResponse>('/baskets', { items });
}

/** POST /api/baskets/reset-defaults[?portfolio=] — сброс к стартовым корзинам. */
export function resetBasketDefaults(portfolio?: CapitalPortfolioId): Promise<BasketsResponse> {
  return apiClient.post<BasketsResponse>(`/baskets/reset-defaults${portfolio ? `?portfolio=${portfolio}` : ''}`);
}
