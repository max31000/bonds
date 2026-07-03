import { apiClient } from './client';
import type { IntradayRange, LivePositionsResponse, PortfolioIntradayResponse } from './types';

/** GET /api/live/positions — лёгкий поллинг цен/дневного изменения по открытым позициям (plan/16 часть A). */
export function fetchLivePositions(): Promise<LivePositionsResponse> {
  return apiClient.get<LivePositionsResponse>('/live/positions');
}

/** GET /api/live/portfolio-intraday?range=1d|5d — ряд суммарной стоимости портфеля внутри дня. */
export function fetchPortfolioIntraday(range: IntradayRange): Promise<PortfolioIntradayResponse> {
  return apiClient.get<PortfolioIntradayResponse>(`/live/portfolio-intraday?range=${range}`);
}
