import { create } from 'zustand';
import { fetchSignals, markAllSignalsRead, markSignalRead } from '../api/signals';
import type { Signal } from '../api/types';

/**
 * Число активных неподтверждённых событий важности Critical+Warning — то, что стоит показать
 * бейджем (навигация, KPI на дашборде). Info («к сведению») и подтверждённые не считаются.
 */
export function countAttention(signals: Signal[]): number {
  return signals.filter(
    (s) => !s.isRead && !s.isResolved && (s.severity === 'Critical' || s.severity === 'Warning'),
  ).length;
}

interface SignalsStore {
  /** Активные события (GET /api/signals). */
  signals: Signal[];
  /** Закрытые события (история), загружаются по требованию. */
  history: Signal[];
  historyLoaded: boolean;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  loadHistory: () => Promise<void>;
  markRead: (id: number) => Promise<void>;
  markAllRead: () => Promise<void>;
}

/**
 * Кэш событий портфеля + действия «прочитано» / «всё прочитано». Паттерн как `usePositionsStore`,
 * без `persist` — серверный кэш на время жизни таба.
 */
export const useSignalsStore = create<SignalsStore>()((set, get) => ({
  signals: [],
  history: [],
  historyLoaded: false,
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const response = await fetchSignals();
      set({ signals: response.signals ?? [], isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить события',
        isLoading: false,
      });
    }
  },
  loadHistory: async () => {
    try {
      const response = await fetchSignals({ includeResolved: true });
      set({
        history: (response.signals ?? []).filter((s) => s.isResolved),
        historyLoaded: true,
      });
    } catch {
      // История второстепенна — при сбое просто остаётся пустой, основной список не страдает.
      set({ history: [], historyLoaded: true });
    }
  },
  markRead: async (id: number) => {
    try {
      await markSignalRead(id);
      set({ signals: get().signals.map((s) => (s.id === id ? { ...s, isRead: true } : s)) });
    } catch {
      // Не критично для UI — событие останется непрочитанным до следующей попытки.
    }
  },
  markAllRead: async () => {
    try {
      await markAllSignalsRead();
      set({ signals: get().signals.map((s) => ({ ...s, isRead: true })) });
    } catch {
      // см. markRead
    }
  },
}));
