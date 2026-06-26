import { create } from 'zustand';
import { fetchCashflow } from '../api/cashflow';
import type { CashflowByPosition, CashflowMonth, NextPayment, PrincipalRelease } from '../api/types';

interface CashflowStore {
  byMonth: CashflowMonth[];
  byPosition: CashflowByPosition[];
  principalReleases: PrincipalRelease[];
  nextPayments: NextPayment[];
  disclaimer: string;
  isLoading: boolean;
  error: string | null;
  load: (to?: string) => Promise<void>;
}

/** Кэш календаря поступлений (GET /api/cashflow). Паттерн как `usePositionsStore`, без `persist`. */
export const useCashflowStore = create<CashflowStore>()((set) => ({
  byMonth: [],
  byPosition: [],
  principalReleases: [],
  nextPayments: [],
  disclaimer: '',
  isLoading: false,
  error: null,
  load: async (to?: string) => {
    set({ isLoading: true, error: null });
    try {
      const response = await fetchCashflow(to ? { to } : undefined);
      set({
        byMonth: response.byMonth,
        byPosition: response.byPosition,
        principalReleases: response.principalReleases,
        nextPayments: response.nextPayments ?? [],
        disclaimer: response.disclaimer,
        isLoading: false,
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить календарь поступлений',
        isLoading: false,
      });
    }
  },
}));
