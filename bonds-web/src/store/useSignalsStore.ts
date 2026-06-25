import { create } from 'zustand';
import { fetchSignals, markSignalRead } from '../api/signals';
import type { Signal } from '../api/types';

interface SignalsStore {
  signals: Signal[];
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  markRead: (id: number) => Promise<void>;
  unreadCount: () => number;
}

/**
 * Кэш списка сигналов (GET /api/signals) + действие «отметить прочитанным».
 * Паттерн как `usePositionsStore`, без `persist` — серверный кэш на время жизни таба.
 */
export const useSignalsStore = create<SignalsStore>()((set, get) => ({
  signals: [],
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const response = await fetchSignals();
      set({ signals: response.signals ?? [], isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить сигналы',
        isLoading: false,
      });
    }
  },
  markRead: async (id: number) => {
    try {
      await markSignalRead(id);
      set({
        signals: get().signals.map((s) => (s.id === id ? { ...s, isRead: true } : s)),
      });
    } catch {
      // Не критично для UI — сигнал просто останется непрочитанным до следующей попытки.
    }
  },
  unreadCount: () => get().signals.filter((s) => !s.isRead).length,
}));
