import { apiClient } from './client';
import type { SettingsResponse, SettingsUpdateRequest, TInvestTokenResponse } from './types';

/** GET /api/settings — текущие пороги триггеров и статус токена T-Invest (plan/09c §B.8). */
export function fetchSettings(): Promise<SettingsResponse> {
  return apiClient.get<SettingsResponse>('/settings');
}

/** PUT /api/settings — обновляет пороги триггеров (без токена — у него отдельный эндпоинт). */
export function updateSettings(body: SettingsUpdateRequest): Promise<SettingsResponse> {
  return apiClient.put<SettingsResponse>('/settings', body);
}

/** PUT /api/settings/tinvest-token — задаёт/обновляет токен T-Invest (write-only, не «эхо»-ится). */
export function updateTInvestToken(token: string): Promise<TInvestTokenResponse> {
  return apiClient.put<TInvestTokenResponse>('/settings/tinvest-token', { token });
}
