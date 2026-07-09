import { create } from 'zustand';
import { fetchComparison, fetchReplacementMatrix } from '../api/recommendations';
import { fetchComposition } from '../api/analytics';
import type { ComparisonRow, MatrixPair, RejectedPair } from '../api/types';

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

  /** Задача 30 часть D.2: имя/эмитент по positionId для ЛЮБОЙ позиции (не только топ-3 слабых
   * звеньев) — нужно секции «Дорогие бумаги — дешёвые соседи по корзине», т.к. Rich-verdict может
   * получить позиция, не попавшая в sellCandidates. */
  positionNamesById: Record<number, string>;

  // Задача 23: секция «Замены» — один запрос матрицы вместо цикла до 6 постов.
  bestPairs: MatrixPair[];
  rejectedPairs: RejectedPair[];
  totalConsideredPairs: number;
  replacementDisclaimer: string;

  isLoading: boolean;
  error: string | null;

  load: () => Promise<void>;
}

/**
 * Стор экрана «Рекомендации» (plan/17, замены переработаны в задаче 23): две секции — слабые
 * звенья (comparison) и замены (GET /replacement-matrix — серверный перебор всех пар). `load()`
 * собирает обе одним заходом. Задача 29: секция «куда вложить сумму» переехала в отдельный
 * компонент <see>BasketConstructor</see> с собственным локальным состоянием (конструктор корзины
 * не нуждается в глобальном сторе — его данные не переиспользуются другими секциями страницы).
 */
export const useRecommendationsStore = create<RecommendationsStore>()((set) => ({
  sellCandidates: [],
  outOfComparison: [],
  comparisonDisclaimer: '',
  positionNamesById: {},

  bestPairs: [],
  rejectedPairs: [],
  totalConsideredPairs: 0,
  replacementDisclaimer: '',

  isLoading: false,
  error: null,

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
      const positionNamesById = Object.fromEntries(
        comparison.rows.map((r) => [r.positionId, r.name ?? r.issuer ?? `Позиция #${r.positionId}`]),
      );

      set({
        sellCandidates: candidates,
        outOfComparison,
        comparisonDisclaimer: comparison.disclaimer,
        positionNamesById,
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
}));
