import { create } from 'zustand';
import { fetchComparison, fetchAllocation, postReplacement } from '../api/recommendations';
import { fetchComposition } from '../api/analytics';
import type { AllocationResponse, ComparisonRow, ReplacementResponse } from '../api/types';

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

/** Одна пара «держать A → переложиться в B» с результатом расчёта. */
export interface ReplacementPair {
  holdPositionId: number;
  targetPositionId: number;
  holdName: string;
  targetName: string;
  result: ReplacementResponse;
}

const TOP_SELL_CANDIDATES = 3;
const TOP_REPLACEMENTS_PER_CANDIDATE = 2;
const MAX_REPLACEMENT_REQUESTS = 6;
const COMPARABLE_DURATION_WINDOW_YEARS = 1.5;
const UPCOMING_HORIZON_DAYS = 90;
/** Пол горизонта замены — чтобы SwitchAnalysisService.Compare не упал на нулевом/отрицательном
 *  горизонте у бумаги с сегодняшним погашением/офертой. Реальный горизонт берётся из daysToHorizon. */
const MIN_HORIZON_DAYS = 1;

/**
 * Горизонт сравнения замены (лет) = ближайший из двух горизонтов (min daysToHorizon hold/target),
 * plan/17 §A.2. Критично для корректности: SwitchAnalysisService считает выгоду ЛИНЕЙНО по горизонту
 * (spreadGainRub = base × yieldSpread × horizonYears), поэтому фиксированный горизонт систематически
 * искажал бы выгоду и фильтр «показывать только выгодные» — особенно для бумаг с близкой офертой.
 */
function horizonYearsFor(holdRow: ComparisonRow, targetRow: ComparisonRow): number {
  const minDays = Math.min(holdRow.daysToHorizon, targetRow.daysToHorizon);
  return Math.max(minDays, MIN_HORIZON_DAYS) / 365;
}

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

/**
 * Для каждого sell-кандидата подбирает до 2 лучших по доходности замен среди остальных сравнимых
 * позиций с сопоставимой дюрацией (±1.5 года — plan/17 §A.2), не более 6 запросов replacement
 * суммарно за загрузку.
 */
function buildReplacementRequests(
  sellCandidates: SellCandidate[],
  allComparable: ComparisonRow[],
): { holdRow: ComparisonRow; targetRow: ComparisonRow }[] {
  const requests: { holdRow: ComparisonRow; targetRow: ComparisonRow }[] = [];

  for (const candidate of sellCandidates) {
    if (requests.length >= MAX_REPLACEMENT_REQUESTS) break;
    const holdRow = candidate.row;

    const targets = allComparable
      .filter((r) => r.positionId !== holdRow.positionId)
      .filter((r) => {
        if (holdRow.modifiedDuration === null || r.modifiedDuration === null) return true;
        return Math.abs(r.modifiedDuration - holdRow.modifiedDuration) <= COMPARABLE_DURATION_WINDOW_YEARS;
      })
      .filter((r) => (r.effectiveYield ?? -Infinity) > (holdRow.effectiveYield ?? -Infinity))
      .sort((a, b) => (b.effectiveYield ?? 0) - (a.effectiveYield ?? 0))
      .slice(0, TOP_REPLACEMENTS_PER_CANDIDATE);

    for (const targetRow of targets) {
      if (requests.length >= MAX_REPLACEMENT_REQUESTS) break;
      requests.push({ holdRow, targetRow });
    }
  }

  return requests;
}

interface RecommendationsStore {
  sellCandidates: SellCandidate[];
  outOfComparison: ComparisonRow[];
  comparisonDisclaimer: string;
  replacements: ReplacementPair[];
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
 * Стор экрана «Рекомендации» (plan/17): три секции — слабые звенья (comparison), замены
 * (replacement, до 6 запросов) и куда вложить сумму (allocation, по требованию через форму).
 * `load()` собирает первые две секции одним заходом; `loadAllocation()` вызывается отдельно
 * (форма суммы) — не блокирует первичную загрузку страницы.
 */
export const useRecommendationsStore = create<RecommendationsStore>()((set, get) => ({
  sellCandidates: [],
  outOfComparison: [],
  comparisonDisclaimer: '',
  replacements: [],
  isLoading: false,
  error: null,

  allocationAmount: 15000,
  allocation: null,
  isAllocationLoading: false,
  allocationError: null,

  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const [comparison, composition] = await Promise.all([fetchComparison(), fetchComposition()]);

      const issuerSharePercent = new Map(composition.byIssuer.map((s) => [s.key, s.sharePercent]));
      const { candidates, outOfComparison } = buildSellCandidates(comparison.rows, issuerSharePercent);
      const allComparable = comparison.rows.filter((r) => !isOutOfComparison(r) && r.effectiveYield !== null);

      const requests = buildReplacementRequests(candidates, allComparable);
      const results = await Promise.all(
        requests.map(({ holdRow, targetRow }) =>
          postReplacement({
            holdPositionId: holdRow.positionId,
            targetPositionId: targetRow.positionId,
            horizonYears: horizonYearsFor(holdRow, targetRow),
          })
            .then((result): ReplacementPair | null =>
              result.netBenefitRub > 0
                ? {
                    holdPositionId: holdRow.positionId,
                    targetPositionId: targetRow.positionId,
                    holdName: holdRow.name ?? holdRow.issuer ?? `Позиция #${holdRow.positionId}`,
                    targetName: targetRow.name ?? targetRow.issuer ?? `Позиция #${targetRow.positionId}`,
                    result,
                  }
                : null,
            )
            .catch(() => null),
        ),
      );

      set({
        sellCandidates: candidates,
        outOfComparison,
        comparisonDisclaimer: comparison.disclaimer,
        replacements: results.filter((r): r is ReplacementPair => r !== null),
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
