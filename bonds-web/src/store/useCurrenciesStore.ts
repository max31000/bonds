import { create } from 'zustand';
import { fetchCurrencies } from '../api/currencies';
import type { CurrencyRow, CurrenciesTotals } from '../api/types';

interface CurrenciesStore {
  currencies: CurrencyRow[];
  totals: CurrenciesTotals | null;
  disclaimer: string;
  /** true после первого завершённого запроса (успешного или нет) — вкладка «Валюта» не мигает до ответа. */
  loaded: boolean;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
}

let loadSeq = 0;

/**
 * Валюта как актив (задача 56): независимый от облигаций и фондов список. Отказ `/api/currencies` не роняет таблицу
 * облигаций — вкладка «Валюта» просто не показывается. Устаревший ответ отбрасывается по номеру запроса.
 */
export const useCurrenciesStore = create<CurrenciesStore>()((set) => ({
  currencies: [],
  totals: null,
  disclaimer: '',
  loaded: false,
  isLoading: false,
  error: null,
  load: async () => {
    const seq = ++loadSeq;
    set({ isLoading: true, error: null });
    try {
      const r = await fetchCurrencies();
      if (seq !== loadSeq) return;
      set({ currencies: r.currencies, totals: r.totals, disclaimer: r.disclaimer, isLoading: false, loaded: true });
    } catch (err) {
      if (seq !== loadSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить валюту', isLoading: false, loaded: true });
    }
  },
}));
