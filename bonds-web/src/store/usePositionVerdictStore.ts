import { create } from 'zustand';
import { fetchPositionVerdicts } from '../api/recommendations';
import type { PositionVerdict } from '../api/types';

interface PositionVerdictStore {
  /** По positionId — вердикт (GET /api/analytics/position-verdicts). */
  verdictsById: Record<number, PositionVerdict>;
  disclaimer: string;
  isLoading: boolean;
  /** Только диагностика/тесты: UI НЕ показывает это как блокирующую ошибку — бейдж просто не рисуется. */
  error: string | null;
  hasLoaded: boolean;
  load: () => Promise<void>;
}

/**
 * Вердикты позиций (задача 49) — ОТДЕЛЬНЫЙ стор по образцу useRelativeValueStore: один bulk-запрос на сессию страницы,
 * словарь по positionId, ошибка ловится здесь и не роняет страницу позиций/карточку (бейдж просто не показывается).
 */
export const usePositionVerdictStore = create<PositionVerdictStore>()((set, get) => ({
  verdictsById: {},
  disclaimer: '',
  isLoading: false,
  error: null,
  hasLoaded: false,

  load: async () => {
    if (get().isLoading || get().hasLoaded) return;
    set({ isLoading: true, error: null });
    try {
      const response = await fetchPositionVerdicts();
      set({
        verdictsById: Object.fromEntries(response.verdicts.map((v) => [v.positionId, v])),
        disclaimer: response.disclaimer,
        isLoading: false,
        hasLoaded: true,
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить вердикты позиций',
        isLoading: false,
        hasLoaded: true,
      });
    }
  },
}));
