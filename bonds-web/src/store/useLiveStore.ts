import { create } from 'zustand';
import type { LivePositionRow } from '../api/types';

interface LiveStore {
  /** По positionId — последнее известное живое состояние позиции (GET /api/live/positions). */
  positionsById: Record<number, LivePositionRow>;
  totalMarketValueRub: number | null;
  asOfUtc: string | null;
  setLivePositions: (positions: LivePositionRow[], totalMarketValueRub: number, asOfUtc: string) => void;
}

/**
 * Живое состояние позиций (plan/16 часть B) — держится отдельно от usePositionsStore (которое
 * кэширует GET /api/positions с расчётными метриками), чтобы поллинг раз в 60 сек не перезаписывал
 * доходности/дюрации/G-спред и не гонял их пересчёт впустую. Positions.tsx мержит оба стора по
 * positionId при рендере (см. usePositionsStore + useLiveStore.positionsById).
 */
export const useLiveStore = create<LiveStore>()((set) => ({
  positionsById: {},
  totalMarketValueRub: null,
  asOfUtc: null,
  setLivePositions: (positions, totalMarketValueRub, asOfUtc) =>
    set({
      positionsById: Object.fromEntries(positions.map((p) => [p.positionId, p])),
      totalMarketValueRub,
      asOfUtc,
    }),
}));
