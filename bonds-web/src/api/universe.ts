import { apiClient } from './client';
import type { MaterializeResponse, UniverseQuery, UniverseResponse } from './types';

/**
 * GET /api/universe — банк облигаций MOEX (задача 26), используется выпадашкой-сравнивалкой
 * (задача 27): пусто → топ-10 самых доходных (скрытые гигиеническим фильтром исключены дефолтом
 * API); search → тот же запрос с текстом (имя/ISIN/SECID).
 */
export function fetchUniverse(query: UniverseQuery): Promise<UniverseResponse> {
  const params = new URLSearchParams();
  if (query.search) params.set('search', query.search);
  params.set('sortBy', query.sortBy ?? 'yield');
  params.set('sortDir', query.sortDir ?? 'desc');
  params.set('limit', String(query.limit ?? 10));

  return apiClient.get<UniverseResponse>(`/universe?${params.toString()}`);
}

/**
 * POST /api/universe/{secid}/materialize — заводит/находит Instrument + котировку для бумаги банка
 * (задача 27 часть A), возвращает полные метрики движка. 422 — бумага не нашлась на MOEX/не
 * облигация/нет данных (текст причины — в error).
 */
export function postMaterialize(secid: string): Promise<MaterializeResponse> {
  return apiClient.post<MaterializeResponse>(`/universe/${encodeURIComponent(secid)}/materialize`);
}
