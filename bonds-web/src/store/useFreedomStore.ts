import { create } from 'zustand';
import { fetchFreedomHoldings } from '../api/brokers';
import type { BrokerHoldingsResponse } from '../api/types';

interface FreedomStore {
  holdings: BrokerHoldingsResponse | null;
  /** true после первого завершённого запроса (успешного или нет). */
  loaded: boolean;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
}

let loadSeq = 0;

/**
 * Счёт Freedom (задача 60): независимый от облигаций, фондов и валюты. Грузится только при `capabilities.freedom`
 * (вкладка «Freedom» на «Позициях»). Устаревший ответ отбрасывается по номеру запроса.
 */
export const useFreedomStore = create<FreedomStore>()((set) => ({
  holdings: null,
  loaded: false,
  isLoading: false,
  error: null,
  load: async () => {
    const seq = ++loadSeq;
    set({ isLoading: true, error: null });
    try {
      const holdings = await fetchFreedomHoldings();
      if (seq !== loadSeq) return;
      set({ holdings, isLoading: false, loaded: true });
    } catch (err) {
      if (seq !== loadSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить счёт Freedom', isLoading: false, loaded: true });
    }
  },
}));
