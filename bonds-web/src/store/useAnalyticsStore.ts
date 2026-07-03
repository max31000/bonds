import { create } from 'zustand';
import { fetchComposition, fetchScatter, fetchXirr, fetchRateScenario, fetchTrajectory, postXirrBackfill } from '../api/analytics';
import type { CompositionResponse, RateScenarioResponse, ScatterResponse, TrajectoryResponse, XirrResponse } from '../api/types';

interface AnalyticsStore {
  scatter: ScatterResponse | null;
  composition: CompositionResponse | null;
  xirr: XirrResponse | null;
  rateScenario: RateScenarioResponse | null;
  trajectory: TrajectoryResponse | null;
  isLoading: boolean;
  error: string | null;
  isBackfilling: boolean;
  load: () => Promise<void>;
  /** Plan/15 §C.3: ручной запуск ретроспективного бэкфилла из пустого состояния XIRR-виджета,
   * затем перезагрузка данных экрана (в т.ч. свежей истории). */
  runXirrBackfill: () => Promise<void>;
}

/**
 * Кэш аналитических виджетов (scatter/composition/xirr/rateScenario/trajectory) — один стор,
 * пять параллельных запросов, чтобы экран `/analytics` грузил все виджеты одним вызовом `load()`.
 * Без `persist`.
 */
export const useAnalyticsStore = create<AnalyticsStore>()((set) => ({
  scatter: null,
  composition: null,
  xirr: null,
  rateScenario: null,
  trajectory: null,
  isLoading: false,
  error: null,
  isBackfilling: false,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const [scatter, composition, xirr, rateScenario, trajectory] = await Promise.all([
        fetchScatter(),
        fetchComposition(),
        fetchXirr(),
        fetchRateScenario(),
        fetchTrajectory(),
      ]);
      set({ scatter, composition, xirr, rateScenario, trajectory, isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить аналитику',
        isLoading: false,
      });
    }
  },
  runXirrBackfill: async () => {
    set({ isBackfilling: true });
    try {
      await postXirrBackfill();
      const xirr = await fetchXirr();
      set({ xirr, isBackfilling: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось восстановить историю',
        isBackfilling: false,
      });
    }
  },
}));
