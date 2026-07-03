import { apiClient } from './client';
import type { PositionDetail, PositionsResponse, PriceHistoryRange } from './types';

/** GET /api/positions — таблица позиций с расчётными метриками (см. plan/08, plan/09a). */
export function fetchPositions(): Promise<PositionsResponse> {
  return apiClient.get<PositionsResponse>('/positions');
}

/** GET /api/positions/{id}?range=... — карточка позиции (plan/19 §A). */
export function fetchPositionDetail(id: number | string, range: PriceHistoryRange = '6m'): Promise<PositionDetail> {
  return apiClient.get<PositionDetail>(`/positions/${id}?range=${range}`);
}
