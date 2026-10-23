import { create } from 'zustand';
import { fetchBaskets, resetBasketDefaults, updateBaskets } from '../api/baskets';
import type { BasketBatchItem, BasketsResponse, CapitalPortfolioId } from '../api/types';

/** Результат записи: ошибка несёт человекочитаемое сообщение бэкенда (422: сумма целей, band ≥ hardBand …). */
export type BasketsSaveResult = { ok: true } | { ok: false; error: string };

interface BasketsStore {
  data: BasketsResponse | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  /** Атомарно меняет цели/полосы корзин; сумма целей портфеля проверяется на бэкенде. */
  save: (items: BasketBatchItem[]) => Promise<BasketsSaveResult>;
  reset: (portfolio?: CapitalPortfolioId) => Promise<BasketsSaveResult>;
}

let loadSeq = 0;

/** Корзины капитала (задача 62): серверные данные, без persist. Устаревший ответ отбрасывается по номеру запроса. */
export const useBasketsStore = create<BasketsStore>()((set) => ({
  data: null,
  isLoading: false,
  error: null,
  load: async () => {
    const seq = ++loadSeq;
    set({ isLoading: true, error: null });
    try {
      const data = await fetchBaskets();
      if (seq !== loadSeq) return;
      set({ data, isLoading: false });
    } catch (err) {
      if (seq !== loadSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить корзины', isLoading: false });
    }
  },
  save: async (items) => {
    try {
      const data = await updateBaskets(items);
      ++loadSeq;
      set({ data, error: null });
      return { ok: true };
    } catch (err) {
      return { ok: false, error: err instanceof Error ? err.message : 'Не удалось сохранить цели' };
    }
  },
  reset: async (portfolio) => {
    try {
      const data = await resetBasketDefaults(portfolio);
      ++loadSeq;
      set({ data, error: null });
      return { ok: true };
    } catch (err) {
      return { ok: false, error: err instanceof Error ? err.message : 'Не удалось сбросить корзины' };
    }
  },
}));
