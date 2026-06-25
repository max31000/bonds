import { apiClient } from './client';
import type { PositionsResponse } from './types';

/** GET /api/positions — таблица позиций с расчётными метриками (см. plan/08, plan/09a). */
export function fetchPositions(): Promise<PositionsResponse> {
  return apiClient.get<PositionsResponse>('/positions');
}
