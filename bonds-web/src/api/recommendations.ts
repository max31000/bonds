import { apiClient } from './client';
import type {
  AllocationResponse,
  ComparisonResponse,
  ReplacementRequest,
  ReplacementResponse,
  ReplacementMatrixResponse,
} from './types';

/** GET /api/analytics/comparison — сравнительная таблица позиций (plan/17 §A, секция «Слабые звенья»). */
export function fetchComparison(): Promise<ComparisonResponse> {
  return apiClient.get<ComparisonResponse>('/analytics/comparison');
}

/**
 * POST /api/analytics/replacement — «держать A vs переложиться в B» (plan/17 §A, секция «Замены»).
 * Задача 23: фронт больше не вызывает этот эндпоинт напрямую (см. fetchReplacementMatrix) — оставлен
 * ради обратной совместимости контракта (интеграционные тесты старого эндпоинта).
 */
export function postReplacement(request: ReplacementRequest): Promise<ReplacementResponse> {
  return apiClient.post<ReplacementResponse>('/analytics/replacement', request);
}

/**
 * GET /api/analytics/replacement-matrix — задача 23: серверный перебор ВСЕХ пар «держать vs
 * переложиться» (портфель + watchlist-таргеты) одним запросом вместо фронтового цикла из до 6
 * постов (buildReplacementRequests, удалён из useRecommendationsStore).
 */
export function fetchReplacementMatrix(): Promise<ReplacementMatrixResponse> {
  return apiClient.get<ReplacementMatrixResponse>('/analytics/replacement-matrix');
}

/**
 * GET /api/analytics/allocation?amountRub=&includeWatchlist= — «куда вложить сумму» (plan/17 §A/§B).
 * Задача 20: includeWatchlist=true (дефолт здесь на фронте) добавляет watchlist-бумаги как кандидатов.
 */
export function fetchAllocation(amountRub: number, includeWatchlist = true): Promise<AllocationResponse> {
  return apiClient.get<AllocationResponse>(
    `/analytics/allocation?amountRub=${amountRub}&includeWatchlist=${includeWatchlist}`,
  );
}
