import { apiClient } from './client';
import type { AllocationResponse, ComparisonResponse, ReplacementRequest, ReplacementResponse } from './types';

/** GET /api/analytics/comparison — сравнительная таблица позиций (plan/17 §A, секция «Слабые звенья»). */
export function fetchComparison(): Promise<ComparisonResponse> {
  return apiClient.get<ComparisonResponse>('/analytics/comparison');
}

/** POST /api/analytics/replacement — «держать A vs переложиться в B» (plan/17 §A, секция «Замены»). */
export function postReplacement(request: ReplacementRequest): Promise<ReplacementResponse> {
  return apiClient.post<ReplacementResponse>('/analytics/replacement', request);
}

/** GET /api/analytics/allocation?amountRub= — «куда вложить сумму» (plan/17 §A/§B). */
export function fetchAllocation(amountRub: number): Promise<AllocationResponse> {
  return apiClient.get<AllocationResponse>(`/analytics/allocation?amountRub=${amountRub}`);
}
