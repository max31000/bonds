import { apiClient } from './client';
import type {
  IssuerFinancialsRequest,
  IssuerFinancialsResponse,
  IssuerFinancialsYear,
  IssuerRating,
  IssuerRatingRequest,
  IssuerRatingsResponse,
} from './types';

/**
 * Ручной ввод данных эмитента для скоркарты надёжности (задача 46 → формы задачи 47). ИНН — строка (10 или
 * 12 цифр, ведущие нули значимы). После записи бэкенд сбрасывает кэш скоркарты — фронт просто перезапрашивает
 * карточку. Ошибки валидации — 422 с текстом в `error` (приходит как `ApiError.message`).
 */

/** GET /api/issuers/{inn}/ratings — введённые вручную рейтинги агентств. */
export function fetchIssuerRatings(inn: string): Promise<IssuerRatingsResponse> {
  return apiClient.get<IssuerRatingsResponse>(`/issuers/${encodeURIComponent(inn)}/ratings`);
}

/** POST /api/issuers/{inn}/ratings — upsert рейтинга по (inn, agency). */
export function saveIssuerRating(inn: string, body: IssuerRatingRequest): Promise<IssuerRating> {
  return apiClient.post<IssuerRating>(`/issuers/${encodeURIComponent(inn)}/ratings`, body);
}

/** DELETE /api/issuers/{inn}/ratings/{agency} — удаляет рейтинг агентства (204; 404, если такого нет). */
export function deleteIssuerRating(inn: string, agency: string): Promise<void> {
  return apiClient.delete<void>(`/issuers/${encodeURIComponent(inn)}/ratings/${encodeURIComponent(agency)}`);
}

/** GET /api/issuers/{inn}/financials — отчётность по годам (ГИР БО + ручная; за год с обеими — ручная). РУБЛИ. */
export function fetchIssuerFinancials(inn: string): Promise<IssuerFinancialsResponse> {
  return apiClient.get<IssuerFinancialsResponse>(`/issuers/${encodeURIComponent(inn)}/financials`);
}

/** POST /api/issuers/{inn}/financials — ручная отчётность за год (РУБЛИ, не тысячи); заменяет прежний ручной ввод за год. */
export function saveIssuerFinancials(inn: string, body: IssuerFinancialsRequest): Promise<IssuerFinancialsYear> {
  return apiClient.post<IssuerFinancialsYear>(`/issuers/${encodeURIComponent(inn)}/financials`, body);
}
