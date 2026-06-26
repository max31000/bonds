import { create } from 'zustand';
import { fetchComposition, fetchScatter, fetchXirr, fetchRateScenario } from '../api/analytics';
import type { CompositionResponse, RateScenarioResponse, ScatterResponse, XirrResponse } from '../api/types';

interface AnalyticsStore {
  scatter: ScatterResponse | null;
  composition: CompositionResponse | null;
  xirr: XirrResponse | null;
  rateScenario: RateScenarioResponse | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
}

/**
 * Кэш аналитических виджетов (scatter/composition/xirr/rateScenario) — один стор, четыре
 * параллельных запроса, чтобы экран `/analytics` грузил все виджеты одним вызовом `load()`.
 * Без `persist`.
 */
export const useAnalyticsStore = create<AnalyticsStore>()((set) => ({
  scatter: null,
  composition: null,
  xirr: null,
  rateScenario: null,
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const [scatter, composition, xirr, rateScenario] = await Promise.all([
        fetchScatter(),
        fetchComposition(),
        fetchXirr(),
        fetchRateScenario(),
      ]);
      set({ scatter, composition, xirr, rateScenario, isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить аналитику',
        isLoading: false,
      });
    }
  },
}));
