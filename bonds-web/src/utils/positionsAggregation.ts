import type { PositionRow } from '../api/types';

/**
 * Итоговая строка таблицы позиций (plan/21 часть B.3) — суммарная стоимость портфеля и
 * средневзвешенные доходность/дюрация (веса — рыночная стоимость позиции).
 * <p>
 * Флоатеры/индексируемые бумаги исключены из средневзвешенной доходности: их «доходность»
 * (currentYield) несравнима с YTM обычных бумаг (плавающий/индексируемый купон, эффективная
 * доходность к погашению не определена так же, как для бумаг с фиксированным купоном) —
 * смешивание в одну взвешенную сумму дало бы вводящее в заблуждение число. Дюрация не имеет
 * этой проблемы (модифицированная дюрация определена и для флоатеров) — считается по всем
 * позициям, где она известна.
 * <p>
 * Вынесено из Positions.tsx в utils ради юнит-теста (react-refresh требует, чтобы файл компонента
 * экспортировал только компоненты, — тот же паттерн, что и scatterChartData.ts).
 */
export interface PositionsTotals {
  /** Сумма marketValueRub по всем позициям. */
  totalMarketValueRub: number;
  /**
   * Средневзвешенная доходность (YTM/currentYield для обычных бумаг), вес — рыночная стоимость.
   * null, если ни одна позиция не даёт сравнимую доходность (нет обычных бумаг с известным YTM).
   */
  weightedYield: number | null;
  /** Средневзвешенная модифицированная дюрация, вес — рыночная стоимость. null, если дюрация нигде не известна. */
  weightedDuration: number | null;
  /** True — хотя бы одна позиция-флоатер/индексируемая была исключена из weightedYield (нужна сноска "* без флоатеров"). */
  hasExcludedFloaters: boolean;
}

/** Эффективная доходность для агрегации — то же правило, что и в таблице (Positions.tsx: effectiveYield). */
function comparableYield(row: PositionRow): number | null {
  if (row.isFloater || row.isIndexed) return null;
  return row.ytmEffective;
}

function weightedAverage(items: { value: number; weight: number }[]): number | null {
  const totalWeight = items.reduce((sum, i) => sum + i.weight, 0);
  if (totalWeight <= 0) return null;
  const weightedSum = items.reduce((sum, i) => sum + i.value * i.weight, 0);
  return weightedSum / totalWeight;
}

/** Считает строку «Итого» для таблицы позиций. Пустой массив → нулевая стоимость и null-метрики. */
export function computePositionsTotals(positions: PositionRow[]): PositionsTotals {
  const totalMarketValueRub = positions.reduce((sum, p) => sum + p.marketValueRub, 0);

  const yieldItems = positions
    .map((p) => ({ value: comparableYield(p), weight: p.marketValueRub }))
    .filter((i): i is { value: number; weight: number } => i.value !== null);

  const durationItems = positions
    .map((p) => ({ value: p.modifiedDuration, weight: p.marketValueRub }))
    .filter((i): i is { value: number; weight: number } => i.value !== null);

  const hasExcludedFloaters = positions.some((p) => p.isFloater || p.isIndexed);

  return {
    totalMarketValueRub,
    weightedYield: weightedAverage(yieldItems),
    weightedDuration: weightedAverage(durationItems),
    hasExcludedFloaters,
  };
}
