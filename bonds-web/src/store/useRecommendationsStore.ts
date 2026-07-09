import { create } from 'zustand';
import { fetchComparison } from '../api/recommendations';
import { fetchComposition } from '../api/analytics';
import type { ComparisonRow } from '../api/types';

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
  // Задача 32 часть A: `'CurrentYield'`, не `'Current'` — зеркалит сериализацию бэкенд-enum
  // YieldKind.ToString() (было расхождение, из-за которого флоатеры протекали в sell-кандидаты).
  return row.dataIncomplete || row.yieldKind === 'CurrentYield';
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

  isLoading: boolean;
  error: string | null;

  load: () => Promise<void>;
}

/**
 * Стор экрана «Рекомендации» (plan/17). Задача 35 часть D.2 — переработка на два главных блока:
 * секция «Замены» (GET /replacement-matrix) удалена со страницы (поглощена блоком 1 — подбор
 * замены на карточке слабой позиции через GET /replacement-candidates, задача 33), поэтому
 * `fetchReplacementMatrix`/`bestPairs`/`rejectedPairs`/`positionNamesById` здесь больше не нужны —
 * `load()` теперь собирает только «слабые звенья» (comparison) + композицию портфеля (для причин
 * концентрации). Блок 2 «куда вложить сумму» (<see>BasketConstructor</see>) и RV-бейджи
 * (<see>useRelativeValueStore</see>) остаются отдельными от этого стора, как и раньше.
 */
export const useRecommendationsStore = create<RecommendationsStore>()((set) => ({
  sellCandidates: [],
  outOfComparison: [],
  comparisonDisclaimer: '',

  isLoading: false,
  error: null,

  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const [comparison, composition] = await Promise.all([fetchComparison(), fetchComposition()]);

      const issuerSharePercent = new Map(composition.byIssuer.map((s) => [s.key, s.sharePercent]));
      const { candidates, outOfComparison } = buildSellCandidates(comparison.rows, issuerSharePercent);

      set({
        sellCandidates: candidates,
        outOfComparison,
        comparisonDisclaimer: comparison.disclaimer,
        isLoading: false,
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить рекомендации',
        isLoading: false,
      });
    }
  },
}));
