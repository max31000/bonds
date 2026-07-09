import { create } from 'zustand';
import { fetchUniverse, fetchUniverseStatus } from '../api/universe';
import type { UniverseRow, UniverseStatus } from '../api/types';

export const SCREENER_PAGE_SIZE = 50;

export type ScreenerSortBy = 'yield' | 'duration' | 'turnover' | 'gspread';
export type ScreenerSortDir = 'asc' | 'desc';

/**
 * Серверные фильтры страницы «Скринер» (plan/28 часть A) — один-в-один параметры GET
 * /api/universe. Задача 32 часть B: `fixedCouponOnly` — исключение, клиентский фильтр (бэкенд не
 * принимает параметр типа купона), по умолчанию ВЫКЛ — флоатеры остаются видны, но помечены.
 */
export interface ScreenerFilters {
  search: string;
  minYield: number | null;
  maxYield: number | null;
  minDurationYears: number | null;
  maxDurationYears: number | null;
  sector: string | null;
  includeHidden: boolean;
  fixedCouponOnly: boolean;
}

export const DEFAULT_SCREENER_FILTERS: ScreenerFilters = {
  search: '',
  minYield: null,
  maxYield: null,
  minDurationYears: null,
  maxDurationYears: null,
  sector: null,
  includeHidden: false,
  fixedCouponOnly: false,
};

interface ScreenerStore {
  filters: ScreenerFilters;
  sortBy: ScreenerSortBy;
  sortDir: ScreenerSortDir;
  offset: number;

  rows: UniverseRow[];
  total: number;
  hiddenCount: number;
  disclaimer: string;
  isLoading: boolean;
  error: string | null;

  status: UniverseStatus | null;
  isStatusLoading: boolean;

  setFilters: (patch: Partial<ScreenerFilters>) => void;
  resetFilters: () => void;
  toggleSort: (key: ScreenerSortBy) => void;
  setOffset: (offset: number) => void;
  /**
   * Задача 32 часть B: «только фикс-купон» — чисто клиентский фильтр (бэкенд GET /api/universe не
   * принимает параметр типа купона), поэтому в отличие от setFilters НЕ дёргает load()/не сбрасывает
   * offset — просто перекрашивает то, что уже загружено (см. фильтрацию в Screener.tsx).
   */
  setFixedCouponOnly: (value: boolean) => void;

  load: () => Promise<void>;
  loadStatus: () => Promise<void>;

  /** Оптимистичное обновление бейджа «в watchlist» после успешного POST /api/watchlist (без перезагрузки страницы). */
  markInWatchlist: (isin: string) => void;
}

/**
 * Стор страницы «Скринер» (plan/28) — весь банк облигаций (GET /api/universe) таблицей с
 * серверными фильтрами/сортировкой/пагинацией. В отличие от MarketComparator (задача 27, топ-10
 * без пагинации), здесь offset реально используется — при смене фильтров/сортировки offset
 * сбрасывается на 0 (иначе пользователь может застрять на пустой странице N при более узкой
 * выдаче).
 */
export const useScreenerStore = create<ScreenerStore>()((set, get) => ({
  filters: DEFAULT_SCREENER_FILTERS,
  sortBy: 'yield',
  sortDir: 'desc',
  offset: 0,

  rows: [],
  total: 0,
  hiddenCount: 0,
  disclaimer: '',
  isLoading: false,
  error: null,

  status: null,
  isStatusLoading: false,

  setFilters: (patch) => {
    set((s) => ({ filters: { ...s.filters, ...patch }, offset: 0 }));
    void get().load();
  },

  resetFilters: () => {
    set({ filters: DEFAULT_SCREENER_FILTERS, offset: 0 });
    void get().load();
  },

  toggleSort: (key) => {
    set((s) => ({
      sortBy: key,
      sortDir: s.sortBy === key ? (s.sortDir === 'asc' ? 'desc' : 'asc') : 'desc',
      offset: 0,
    }));
    void get().load();
  },

  setOffset: (offset) => {
    set({ offset });
    void get().load();
  },

  setFixedCouponOnly: (value) => {
    set((s) => ({ filters: { ...s.filters, fixedCouponOnly: value } }));
  },

  load: async () => {
    const { filters, sortBy, sortDir, offset } = get();
    set({ isLoading: true, error: null });
    try {
      const response = await fetchUniverse({
        search: filters.search || undefined,
        minYield: filters.minYield ?? undefined,
        maxYield: filters.maxYield ?? undefined,
        minDurationYears: filters.minDurationYears ?? undefined,
        maxDurationYears: filters.maxDurationYears ?? undefined,
        sector: filters.sector ?? undefined,
        includeHidden: filters.includeHidden || undefined,
        sortBy,
        sortDir,
        limit: SCREENER_PAGE_SIZE,
        offset,
      });
      set({
        rows: response.rows,
        total: response.total,
        hiddenCount: response.hiddenCount,
        disclaimer: response.disclaimer,
        isLoading: false,
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить банк облигаций',
        isLoading: false,
      });
    }
  },

  loadStatus: async () => {
    set({ isStatusLoading: true });
    try {
      const status = await fetchUniverseStatus();
      set({ status, isStatusLoading: false });
    } catch {
      // Статусная строка необязательна для работы страницы — молча оставляем null (таблица уже
      // несёт свой disclaimer/hiddenCount из того же запроса GET /api/universe).
      set({ isStatusLoading: false });
    }
  },

  markInWatchlist: (isin) => {
    set((s) => ({
      rows: s.rows.map((r) => (r.isin === isin ? { ...r, inWatchlist: true } : r)),
    }));
  },
}));
