import { apiClient } from './client';
import type { SettingsResponse, SettingsUpdateRequest } from './types';

/** GET /api/settings — текущие пороги триггеров и статус токена T-Invest. */
export function fetchSettings(): Promise<SettingsResponse> {
  return apiClient.get<SettingsResponse>('/settings');
}

/** PUT /api/settings — обновляет пороги триггеров (без токена — у него отдельный эндпоинт). */
export function updateSettings(body: SettingsUpdateRequest): Promise<SettingsResponse> {
  return apiClient.put<SettingsResponse>('/settings', body);
}
