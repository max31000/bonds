import { create } from 'zustand';
import { fetchSyncStatus, runSync } from '../api/sync';
import type { SyncRunResult } from '../api/types';

interface SyncStore {
  isRunning: boolean;
  lastResult: SyncRunResult | null;
  error: string | null;
  /** Запускает форс-синхронизацию и опрашивает /sync/status, пока она не завершится. */
  triggerSync: () => Promise<SyncRunResult | null>;
  refreshStatus: () => Promise<void>;
}

const POLL_INTERVAL_MS = 1500;

function sleep(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Состояние форс-обновления (POST /api/sync, GET /api/sync/status, plan/09c §B.7).
 * Кнопка доступна из шапки/навигации на любом экране — состояние держим в общем сторе,
 * не локальным хуком, чтобы не дублировать спиннер/дизейбл в нескольких местах.
 */
export const useSyncStore = create<SyncStore>()((set, get) => ({
  isRunning: false,
  lastResult: null,
  error: null,
  refreshStatus: async () => {
    try {
      const status = await fetchSyncStatus();
      set({ isRunning: status.isRunning });
    } catch {
      // Статус не критичен — следующий опрос/клик попробует снова.
    }
  },
  triggerSync: async () => {
    if (get().isRunning) return null;
    set({ isRunning: true, error: null });
    try {
      const result = await runSync();
      set({ lastResult: result });

      // alreadyRunning=true означает, что синк уже шёл (запущен планировщиком/другой вкладкой) —
      // опрашиваем /sync/status, пока он не завершится, чтобы кнопка корректно разблокировалась.
      while (get().isRunning) {
        const status = await fetchSyncStatus();
        if (!status.isRunning) break;
        await sleep(POLL_INTERVAL_MS);
      }

      set({ isRunning: false });
      return result;
    } catch (err) {
      set({
        isRunning: false,
        error: err instanceof Error ? err.message : 'Не удалось запустить обновление данных',
      });
      return null;
    }
  },
}));
