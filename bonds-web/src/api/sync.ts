import { apiClient } from './client';
import type { SyncRunResult, SyncStatus } from './types';

/** POST /api/sync — запускает форс-синхронизацию с T-Invest/MOEX (plan/09c §B.7). */
export function runSync(): Promise<SyncRunResult> {
  return apiClient.post<SyncRunResult>('/sync');
}

/** GET /api/sync/status — текущий статус планировщика (идёт ли синк сейчас и т.д.). */
export function fetchSyncStatus(): Promise<SyncStatus> {
  return apiClient.get<SyncStatus>('/sync/status');
}
