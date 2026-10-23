import { apiClient } from './client';
import type { Capabilities, CredentialPutRequest, CredentialRow } from './types';

/** GET /api/credentials — строка на каждого провайдера реестра (незаданные — status: 'missing'). */
export function fetchCredentials(): Promise<CredentialRow[]> {
  return apiClient.get<CredentialRow[]>('/credentials');
}

/** PUT /api/credentials/{provider} — валидирует и сохраняет секрет (422 с `error` — не прошёл проверку, ничего не сохранено). */
export function putCredential(provider: string, body: CredentialPutRequest): Promise<CredentialRow> {
  return apiClient.put<CredentialRow>(`/credentials/${encodeURIComponent(provider)}`, body);
}

/** DELETE /api/credentials/{provider} → 204. */
export function deleteCredential(provider: string): Promise<void> {
  return apiClient.delete<void>(`/credentials/${encodeURIComponent(provider)}`);
}

/** GET /api/capabilities — какие разделы функционала включены подключёнными секретами. */
export function fetchCapabilities(): Promise<Capabilities> {
  return apiClient.get<Capabilities>('/capabilities');
}
