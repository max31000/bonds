import { apiClient } from './client';
import type { WatchlistCreateRequest, WatchlistCreateResponse, WatchlistResponse } from './types';

/** GET /api/watchlist — ручной список бумаг вне текущих позиций, с метриками (plan/20 §A). */
export function fetchWatchlist(): Promise<WatchlistResponse> {
  return apiClient.get<WatchlistResponse>('/watchlist');
}

/** POST /api/watchlist — добавить ISIN (422, если не найден на MOEX/не облигация). */
export function postWatchlistItem(request: WatchlistCreateRequest): Promise<WatchlistCreateResponse> {
  return apiClient.post<WatchlistCreateResponse>('/watchlist', request);
}

/** DELETE /api/watchlist/{id}. */
export function deleteWatchlistItem(id: number): Promise<void> {
  return apiClient.delete<void>(`/watchlist/${id}`);
}
