import { create } from 'zustand';
import { fetchIncome, fetchIncomeLedger } from '../api/income';
import type { IncomeAssetKind, IncomeLedgerResponse, IncomeQuery, IncomeResponse } from '../api/types';

export interface LedgerState {
  status: 'loading' | 'ok' | 'error';
  data?: IncomeLedgerResponse;
  error?: string;
}

/** Ключ кэша журнала: `assetKind:assetId`. */
export function ledgerKey(kind: IncomeAssetKind, assetId: number): string {
  return `${kind}:${assetId}`;
}

interface IncomeStore {
  data: IncomeResponse | null;
  isLoading: boolean;
  error: string | null;
  /** Самый ранний год среди виденных позиций — верхняя граница списка годов в фильтре. */
  firstYear: number | null;
  /** Кэш раскрытых журналов; ошибка журнала не роняет сводку и наоборот. */
  ledgers: Record<string, LedgerState>;
  load: (q: IncomeQuery) => Promise<void>;
  loadLedger: (kind: IncomeAssetKind, assetId: number) => Promise<void>;
}

let requestSeq = 0;

/**
 * Доходы (задача 54): сводка и журналы операций грузятся независимо. Журнал актива не зависит от фильтров
 * страницы, поэтому кэш переживает смену периода; повторное раскрытие запрос не повторяет (кроме ошибки).
 * Устаревший ответ (быстрая смена фильтров) отбрасывается по номеру запроса.
 */
export const useIncomeStore = create<IncomeStore>()((set, get) => ({
  data: null,
  isLoading: false,
  error: null,
  firstYear: null,
  ledgers: {},
  load: async (q) => {
    const seq = ++requestSeq;
    set({ isLoading: true, error: null });
    try {
      const data = await fetchIncome(q);
      if (seq !== requestSeq) return;
      const years = data.positions.map((p) => Number(p.firstDate.slice(0, 4))).filter((y) => Number.isFinite(y));
      const prev = get().firstYear;
      const min = years.length > 0 ? Math.min(...years, ...(prev === null ? [] : [prev])) : prev;
      set({ data, isLoading: false, firstYear: min });
    } catch (err) {
      if (seq !== requestSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить доходы', isLoading: false });
    }
  },
  loadLedger: async (kind, assetId) => {
    const key = ledgerKey(kind, assetId);
    const cur = get().ledgers[key];
    if (cur && cur.status !== 'error') return;
    set((s) => ({ ledgers: { ...s.ledgers, [key]: { status: 'loading' } } }));
    try {
      const data = await fetchIncomeLedger(kind, assetId);
      set((s) => ({ ledgers: { ...s.ledgers, [key]: { status: 'ok', data } } }));
    } catch (err) {
      set((s) => ({
        ledgers: {
          ...s.ledgers,
          [key]: { status: 'error', error: err instanceof Error ? err.message : 'Не удалось загрузить журнал операций' },
        },
      }));
    }
  },
}));
