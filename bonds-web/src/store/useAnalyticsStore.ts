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
  /** Загрузить все виджеты; `includeFunds` (по умолчанию true) относится к композиции и сценариям ставок. */
  load: (includeFunds?: boolean) => Promise<void>;
  /** Перезагрузить только виджеты, зависящие от переключателя «С фондами / Только облигации» (композиция + сценарии ставок). */
  loadFundScope: (includeFunds: boolean) => Promise<void>;
  /** Ручной запуск ретроспективного бэкфилла из пустого состояния XIRR-виджета,
   * затем перезагрузка данных экрана (в т.ч. свежей истории). */
  runXirrBackfill: () => Promise<void>;
}

/**
 * Кэш аналитических виджетов (scatter/composition/xirr/rateScenario/trajectory) — один стор,
 * пять параллельных запросов, чтобы экран `/analytics` грузил все виджеты одним вызовом `load()`.
 * Без `persist`.
 */
// Номер последнего запроса переключателя фондов: быстрый щелчок «С фондами/Только облигации» не должен дать
// более медленному старому ответу перезаписать новый (тот же приём, что loadSeq в useFundsStore).
let fundScopeSeq = 0;

export const useAnalyticsStore = create<AnalyticsStore>()((set) => ({
  scatter: null,
  composition: null,
  xirr: null,
  rateScenario: null,
  trajectory: null,
  isLoading: false,
  error: null,
  isBackfilling: false,
  load: async (includeFunds = true) => {
    set({ isLoading: true, error: null });
    try {
      const [scatter, composition, xirr, rateScenario, trajectory] = await Promise.all([
        fetchScatter(),
        fetchComposition({ includeFunds }),
        fetchXirr(),
        fetchRateScenario({ includeFunds }),
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
  loadFundScope: async (includeFunds) => {
    const seq = ++fundScopeSeq;
    try {
      const [composition, rateScenario] = await Promise.all([
        fetchComposition({ includeFunds }),
        fetchRateScenario({ includeFunds }),
      ]);
      if (seq !== fundScopeSeq) return;
      set({ composition, rateScenario });
    } catch (err) {
      if (seq !== fundScopeSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить аналитику' });
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
