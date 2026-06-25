import { create } from 'zustand';
import { fetchComposition, fetchScatter, fetchXirr } from '../api/analytics';
import type { CompositionResponse, ScatterResponse, XirrResponse } from '../api/types';

interface AnalyticsStore {
  scatter: ScatterResponse | null;
  composition: CompositionResponse | null;
  xirr: XirrResponse | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
}

/**
 * Кэш аналитических виджетов (scatter/composition/xirr) — один стор, три параллельных запроса,
 * чтобы экран `/analytics` грузил все три виджета одним вызовом `load()`. Без `persist`.
 */
export const useAnalyticsStore = create<AnalyticsStore>()((set) => ({
  scatter: null,
  composition: null,
  xirr: null,
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const [scatter, composition, xirr] = await Promise.all([
        fetchScatter(),
        fetchComposition(),
        fetchXirr(),
      ]);
      set({ scatter, composition, xirr, isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить аналитику',
        isLoading: false,
      });
    }
  },
}));
