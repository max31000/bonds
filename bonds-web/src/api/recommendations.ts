import { apiClient } from './client';
import type {
  AllocationResponse,
  AllocationSource,
  BasketRequest,
  BasketResponse,
  ComparisonResponse,
  ReplacementRequest,
  ReplacementResponse,
  ReplacementCandidatesMode,
  ReplacementCandidatesResponse,
  RelativeValueResponse,
} from './types';

/** GET /api/analytics/comparison — сравнительная таблица позиций (plan/17 §A, секция «Слабые звенья»). */
export function fetchComparison(): Promise<ComparisonResponse> {
  return apiClient.get<ComparisonResponse>('/analytics/comparison');
}

/**
 * POST /api/analytics/replacement — «держать A vs переложиться в B» (plan/17 §A). Задача 35:
 * единый путь оценки выгоды выбранного кандидата — вызывается из блока 1 после materialize
 * выбранного кандидата (mode=market/rv/поиск), тот же путь, что раньше использовал MarketComparator.
 */
export function postReplacement(request: ReplacementRequest): Promise<ReplacementResponse> {
  return apiClient.post<ReplacementResponse>('/analytics/replacement', request);
}

/**
 * GET /api/analytics/replacement-candidates?positionId=&mode= — задача 33 часть B / задача 35 §A:
 * единый источник кандидатов-замен ОДНОЙ позиции портфеля для блока 1 «Рекомендаций» —
 * mode=market (самые доходные фикс-купонные бумаги банка) / mode=rv (дешёвые соседи по корзине
 * сектор×дюрация позиции). Дешёвая банк-статистика + информационные риск-сигналы; точную выгоду
 * выбранного кандидата считает POST /analytics/replacement (см. doc-comment postReplacement).
 */
export function fetchReplacementCandidates(
  positionId: number,
  mode: ReplacementCandidatesMode,
  limit?: number,
): Promise<ReplacementCandidatesResponse> {
  const params = new URLSearchParams({ positionId: String(positionId), mode });
  if (limit !== undefined) params.set('limit', String(limit));
  return apiClient.get<ReplacementCandidatesResponse>(`/analytics/replacement-candidates?${params.toString()}`);
}

/**
 * GET /api/analytics/allocation?amountRub=&source=&includeWatchlist= — «куда вложить сумму»
 * (plan/17 §A/§B, задача 34 добавила source). source по умолчанию "portfolio" (прежнее поведение —
 * докупка только позиций счёта); "market"/"recommended" — вся биржа/отфильтрованная выборка банка,
 * не зависят от includeWatchlist (бэкенд игнорирует его вне source=portfolio).
 */
export function fetchAllocation(
  amountRub: number,
  options?: { source?: AllocationSource; includeWatchlist?: boolean },
): Promise<AllocationResponse> {
  const source = options?.source ?? 'portfolio';
  const includeWatchlist = options?.includeWatchlist ?? true;
  const params = new URLSearchParams({
    amountRub: String(amountRub),
    source,
    includeWatchlist: String(includeWatchlist),
  });
  return apiClient.get<AllocationResponse>(`/analytics/allocation?${params.toString()}`);
}

/**
 * POST /api/analytics/basket — задача 29: конструктор портфеля. Сумма + строки корзины
 * {instrumentId, weightFraction} (доля 0..1, Σ ≤ 1) → штуки/стоимость (BasketDto) + what-if
 * всего портфеля до/после покупки (WhatIfDto). 422 при невалидных данных (сумма/веса/инструмент).
 */
export function postBasket(request: BasketRequest): Promise<BasketResponse> {
  return apiClient.post<BasketResponse>('/analytics/basket', request);
}

/**
 * GET /api/analytics/relative-value — задача 30: «дорого/дёшево ОТНОСИТЕЛЬНО СВОИХ» (сектор ×
 * дюрационная корзина), не «где YTM больше». Загружается ОТДЕЛЬНО от useRecommendationsStore.load()
 * (не через Promise.all) — отказ этого эндпоинта не должен ронять остальную страницу рекомендаций
 * (план часть D: индикатор просто не показывается при ошибке).
 */
export function fetchRelativeValue(): Promise<RelativeValueResponse> {
  return apiClient.get<RelativeValueResponse>('/analytics/relative-value');
}
