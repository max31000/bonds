import { create } from 'zustand';
import { fetchComparison, fetchAllocation, fetchReplacementMatrix } from '../api/recommendations';
import { fetchComposition } from '../api/analytics';
import type { AllocationResponse, ComparisonRow, MatrixPair, RejectedPair } from '../api/types';

/** Причина попадания позиции в «слабые звенья» — отображается бейджем на карточке. */
export interface SellReason {
  kind: 'belowMedian' | 'upcomingHorizon' | 'concentration';
  label: string;
}

/** Sell-кандидат — строка сравнения + причины, почему она попала в топ-3 слабых звеньев. */
export interface SellCandidate {
  row: ComparisonRow;
  reasons: SellReason[];
}

const TOP_SELL_CANDIDATES = 3;
const UPCOMING_HORIZON_DAYS = 90;

/** Позиция несравнима по доходности «вне сравнения» (plan/17 §A.1: floater/indexed/dataIncomplete). */
function isOutOfComparison(row: ComparisonRow): boolean {
  return row.dataIncomplete || row.yieldKind === 'Current';
}

function median(values: number[]): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
}

/**
 * Строит топ-3 sell-кандидатов из строк сравнения (plan/17 §A.1): позиции с наименьшей доходностью
 * относительно медианы портфеля, с причинами-бейджами. Флоатеры/индексируемые/dataIncomplete не
 * ранжируются (несравнимы по доходности — см. PositionComparisonService.YieldDisclaimer) и
 * возвращаются отдельно как «вне сравнения».
 */
function buildSellCandidates(
  rows: ComparisonRow[],
  issuerSharePercent: Map<string, number>,
): { candidates: SellCandidate[]; outOfComparison: ComparisonRow[] } {
  const comparable = rows.filter((r) => !isOutOfComparison(r) && r.effectiveYield !== null);
  const outOfComparison = rows.filter(isOutOfComparison);

  const yields = comparable.map((r) => r.effectiveYield!);
  const medianYield = median(yields);

  const ranked = [...comparable].sort((a, b) => (a.effectiveYield ?? 0) - (b.effectiveYield ?? 0));

  const candidates: SellCandidate[] = ranked.slice(0, TOP_SELL_CANDIDATES).map((row) => {
    const reasons: SellReason[] = [];
    const gapPp = (medianYield - (row.effectiveYield ?? 0)) * 100;
    if (gapPp > 0) {
      reasons.push({
        kind: 'belowMedian',
        label: `доходность ниже медианы портфеля на ${gapPp.toFixed(1)} п.п.`,
      });
    }
    if (row.daysToHorizon <= UPCOMING_HORIZON_DAYS) {
      reasons.push({
        kind: 'upcomingHorizon',
        label: `${row.calculatedToOffer ? 'оферта' : 'погашение'} через ${row.daysToHorizon} дн.`,
      });
    }
    const share = row.issuer ? issuerSharePercent.get(row.issuer) : undefined;
    if (share !== undefined && share > 0) {
      reasons.push({
        kind: 'concentration',
        label: `доля в портфеле ${share.toFixed(1)}%`,
      });
    }
    return { row, reasons };
  });

  return { candidates, outOfComparison };
}

interface RecommendationsStore {
  sellCandidates: SellCandidate[];
  outOfComparison: ComparisonRow[];
  comparisonDisclaimer: string;

  // Задача 23: секция «Замены» — один запрос матрицы вместо цикла до 6 постов.
  bestPairs: MatrixPair[];
  rejectedPairs: RejectedPair[];
  totalConsideredPairs: number;
  replacementDisclaimer: string;

  isLoading: boolean;
  error: string | null;

  allocationAmount: number;
  allocation: AllocationResponse | null;
  isAllocationLoading: boolean;
  allocationError: string | null;

  load: () => Promise<void>;
  setAllocationAmount: (amount: number) => void;
  loadAllocation: (amountRub?: number) => Promise<void>;
}

/**
 * Стор экрана «Рекомендации» (plan/17, замены переработаны в задаче 23): три секции — слабые
 * звенья (comparison), замены (GET /replacement-matrix — серверный перебор всех пар) и куда
 * вложить сумму (allocation, по требованию через форму). `load()` собирает первые две секции
 * одним заходом; `loadAllocation()` вызывается отдельно (форма суммы) — не блокирует первичную
 * загрузку страницы.
 */
export const useRecommendationsStore = create<RecommendationsStore>()((set, get) => ({
  sellCandidates: [],
  outOfComparison: [],
  comparisonDisclaimer: '',

  bestPairs: [],
  rejectedPairs: [],
  totalConsideredPairs: 0,
  replacementDisclaimer: '',

  isLoading: false,
  error: null,

  allocationAmount: 15000,
  allocation: null,
  isAllocationLoading: false,
  allocationError: null,

  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const [comparison, composition, matrix] = await Promise.all([
        fetchComparison(),
        fetchComposition(),
        fetchReplacementMatrix(),
      ]);

      const issuerSharePercent = new Map(composition.byIssuer.map((s) => [s.key, s.sharePercent]));
      const { candidates, outOfComparison } = buildSellCandidates(comparison.rows, issuerSharePercent);

      set({
        sellCandidates: candidates,
        outOfComparison,
        comparisonDisclaimer: comparison.disclaimer,
        bestPairs: matrix.bestPairs,
        rejectedPairs: matrix.rejectedPairs,
        totalConsideredPairs: matrix.totalConsideredPairs,
        replacementDisclaimer: matrix.disclaimer,
        isLoading: false,
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить рекомендации',
        isLoading: false,
      });
    }
  },

  setAllocationAmount: (amount) => set({ allocationAmount: amount }),

  loadAllocation: async (amountRub) => {
    const amount = amountRub ?? get().allocationAmount;
    set({ isAllocationLoading: true, allocationError: null });
    try {
      const allocation = await fetchAllocation(amount);
      set({ allocation, isAllocationLoading: false });
    } catch (err) {
      set({
        allocationError: err instanceof Error ? err.message : 'Не удалось рассчитать распределение',
        isAllocationLoading: false,
      });
    }
  },
}));
