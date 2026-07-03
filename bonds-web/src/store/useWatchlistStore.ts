import { create } from 'zustand';
import { fetchWatchlist, postWatchlistItem, deleteWatchlistItem } from '../api/watchlist';
import type { WatchlistItem } from '../api/types';

interface WatchlistStore {
  items: WatchlistItem[];
  disclaimer: string;
  isLoading: boolean;
  error: string | null;

  isAdding: boolean;
  addError: string | null;

  load: () => Promise<void>;
  add: (isin: string, note?: string) => Promise<boolean>;
  remove: (id: number) => Promise<void>;
  clearAddError: () => void;
}

/**
 * Секция «Watchlist» на странице «Рекомендации» (plan/20 §B.1). Отдельный стор (не часть
 * useRecommendationsStore) — watchlist также используется независимо на экране аналитики
 * (scatter watchlist-точки читаются из ответа /api/analytics/scatter напрямую, не отсюда).
 */
export const useWatchlistStore = create<WatchlistStore>()((set, get) => ({
  items: [],
  disclaimer: '',
  isLoading: false,
  error: null,
  isAdding: false,
  addError: null,

  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const response = await fetchWatchlist();
      set({ items: response.items, disclaimer: response.disclaimer, isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить watchlist',
        isLoading: false,
      });
    }
  },

  add: async (isin: string, note?: string) => {
    set({ isAdding: true, addError: null });
    try {
      await postWatchlistItem({ isin, note });
      await get().load();
      set({ isAdding: false });
      return true;
    } catch (err) {
      set({
        addError: err instanceof Error ? err.message : 'Не удалось добавить бумагу',
        isAdding: false,
      });
      return false;
    }
  },

  remove: async (id: number) => {
    const previous = get().items;
    // Оптимистичное удаление — заметно отзывчивее на медленном соединении; откатывается при ошибке.
    set({ items: previous.filter((i) => i.id !== id) });
    try {
      await deleteWatchlistItem(id);
    } catch {
      set({ items: previous });
    }
  },

  clearAddError: () => set({ addError: null }),
}));
