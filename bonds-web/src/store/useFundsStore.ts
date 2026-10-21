import { create } from 'zustand';
import { fetchFundDetail, fetchFunds, updateFundSettings } from '../api/funds';
import type { FundDetail, FundForecastMethod, FundRow, FundsTotals } from '../api/types';

interface FundsStore {
  funds: FundRow[];
  totals: FundsTotals | null;
  disclaimer: string;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;

  detail: FundDetail | null;
  detailLoading: boolean;
  detailError: string | null;
  settingsSaving: boolean;
  settingsError: string | null;
  loadDetail: (fundId: number | string) => Promise<void>;
  /** Сохраняет метод прогноза и рефетчит карточку (оценка пересчитывается на бэкенде). */
  saveSettings: (fundId: number | string, method: FundForecastMethod, benchmarkIndex?: string | null) => Promise<void>;
}

let loadSeq = 0;

/**
 * Фонды (задача 53): список и карточка грузятся независимо друг от друга и от облигационных сторов — отказ `/api/funds`
 * не роняет таблицу облигаций. Серверный кэш на время жизни таба, без persist.
 */
export const useFundsStore = create<FundsStore>()((set, get) => ({
  funds: [],
  totals: null,
  disclaimer: '',
  isLoading: false,
  error: null,
  load: async () => {
    // Устаревший ответ (повторный load до завершения предыдущего) отбрасывается по номеру запроса — как в useIncomeStore.
    const seq = ++loadSeq;
    set({ isLoading: true, error: null });
    try {
      const r = await fetchFunds();
      if (seq !== loadSeq) return;
      set({ funds: r.funds, totals: r.totals, disclaimer: r.disclaimer, isLoading: false });
    } catch (err) {
      if (seq !== loadSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить фонды', isLoading: false });
    }
  },

  detail: null,
  detailLoading: false,
  detailError: null,
  settingsSaving: false,
  settingsError: null,
  loadDetail: async (fundId) => {
    set({ detailLoading: true, detailError: null, detail: null, settingsError: null });
    try {
      set({ detail: await fetchFundDetail(fundId), detailLoading: false });
    } catch (err) {
      set({ detailError: err instanceof Error ? err.message : 'Не удалось загрузить фонд', detailLoading: false });
    }
  },
  saveSettings: async (fundId, method, benchmarkIndex) => {
    set({ settingsSaving: true, settingsError: null });
    try {
      await updateFundSettings(fundId, { forecastMethod: method, benchmarkIndex: benchmarkIndex ?? get().detail?.settings.benchmarkIndex ?? null });
      const detail = await fetchFundDetail(fundId);
      set({ detail, settingsSaving: false });
    } catch (err) {
      set({ settingsError: err instanceof Error ? err.message : 'Не удалось сохранить настройки', settingsSaving: false });
    }
  },
}));
