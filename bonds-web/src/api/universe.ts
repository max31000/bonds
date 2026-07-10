import { apiClient } from './client';
import type { MaterializeResponse, UniverseQuery, UniverseResponse, UniverseStatus } from './types';

/**
 * GET /api/universe — банк облигаций MOEX (задача 26). Изначально (задача 27) использовалась
 * только выпадашкой-сравнивалкой (search + сортировка + limit); задача 28 (страница «Скринер»)
 * расширяет тот же клиент остальными серверными фильтрами и пагинацией (offset) — параметры
 * зеркалят сигнатуру `UniverseEndpoints.GetUniverse`. Не задан — параметр не отправляется, бэкенд
 * применяет свой дефолт (см. doc-comment эндпоинта).
 */
export function fetchUniverse(query: UniverseQuery): Promise<UniverseResponse> {
  const params = new URLSearchParams();
  if (query.search) params.set('search', query.search);
  if (query.minYield !== undefined) params.set('minYield', String(query.minYield));
  if (query.maxYield !== undefined) params.set('maxYield', String(query.maxYield));
  if (query.minDurationYears !== undefined) params.set('minDurationYears', String(query.minDurationYears));
  if (query.maxDurationYears !== undefined) params.set('maxDurationYears', String(query.maxDurationYears));
  if (query.sector) params.set('sector', query.sector);
  if (query.includeHidden !== undefined) params.set('includeHidden', String(query.includeHidden));
  if (query.reliability) params.set('reliability', query.reliability);
  params.set('sortBy', query.sortBy ?? 'yield');
  params.set('sortDir', query.sortDir ?? 'desc');
  params.set('limit', String(query.limit ?? 10));
  if (query.offset !== undefined) params.set('offset', String(query.offset));

  return apiClient.get<UniverseResponse>(`/universe?${params.toString()}`);
}

/** GET /api/universe/status — сводка банка облигаций (задача 28, статусная строка скринера). */
export function fetchUniverseStatus(): Promise<UniverseStatus> {
  return apiClient.get<UniverseStatus>('/universe/status');
}

/**
 * POST /api/universe/{secid}/materialize — заводит/находит Instrument + котировку для бумаги банка
 * (задача 27 часть A), возвращает полные метрики движка. 422 — бумага не нашлась на MOEX/не
 * облигация/нет данных (текст причины — в error).
 */
export function postMaterialize(secid: string): Promise<MaterializeResponse> {
  return apiClient.post<MaterializeResponse>(`/universe/${encodeURIComponent(secid)}/materialize`);
}
