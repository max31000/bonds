import { create } from 'zustand';
import { fetchPositions } from '../api/positions';
import type { PositionRow } from '../api/types';

interface PositionsStore {
  positions: PositionRow[];
  disclaimer: string;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
}

/**
 * Кэш списка позиций (GET /api/positions). Паттерн как `useAuthStore`, но без `persist` —
 * это не сессионные данные, а серверный кэш на время жизни таба.
 */
export const usePositionsStore = create<PositionsStore>()((set) => ({
  positions: [],
  disclaimer: '',
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const response = await fetchPositions();
      set({ positions: response.positions, disclaimer: response.disclaimer, isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить позиции',
        isLoading: false,
      });
    }
  },
}));
