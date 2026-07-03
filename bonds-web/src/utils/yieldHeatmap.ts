/**
 * Heatmap-подсветка колонки «Доходность» в таблице позиций (plan/21 часть B.1) — фон ячейки от
 * бледного к насыщенному по перцентилю доходности внутри портфеля (не по абсолютному значению:
 * "хорошая" доходность зависит от состава конкретного портфеля, перцентиль нагляднее).
 * <p>
 * Один цветовой тон (violet, тот же primaryColor темы) с растущей alpha — работает и в светлой,
 * и в тёмной теме без отдельной палитры на каждую (Mantine `color-mix` через CSS var + alpha).
 * Вынесено в utils для юнит-теста (чистая функция от массива чисел).
 */

/** Возвращает перцентиль (0..1) значения `value` в отсортированном массиве `sortedValues` (доля значений ≤ value). */
function percentileRank(value: number, sortedValues: number[]): number {
  if (sortedValues.length <= 1) return 0.5;
  let countBelow = 0;
  for (const v of sortedValues) {
    if (v < value) countBelow += 1;
  }
  // Линейная интерполяция внутри группы равных значений, чтобы одинаковые доходности не получали
  // грубо разный перцентиль в зависимости от порядка обхода.
  let countEqual = 0;
  for (const v of sortedValues) {
    if (v === value) countEqual += 1;
  }
  return (countBelow + countEqual / 2) / sortedValues.length;
}

const MIN_ALPHA = 0.06;
const MAX_ALPHA = 0.32;

/**
 * Строит карту `значение доходности → CSS-цвет фона ячейки` для набора сравнимых доходностей
 * (обычные бумаги; floater/indexed передавать не нужно — вызывающий код их не красит).
 * Возвращает функцию (не Map по ключу-числу — значения могут повторяться и плавать по float
 * precision), которую вызывающий код применяет к каждой строке через индекс.
 */
export function buildYieldHeatmapScale(values: number[]): (value: number) => string | undefined {
  const finite = values.filter((v) => Number.isFinite(v));
  if (finite.length === 0) return () => undefined;

  const sorted = [...finite].sort((a, b) => a - b);

  return (value: number) => {
    if (!Number.isFinite(value)) return undefined;
    const rank = percentileRank(value, sorted);
    const alpha = MIN_ALPHA + rank * (MAX_ALPHA - MIN_ALPHA);
    return `color-mix(in srgb, var(--mantine-color-violet-6) ${Math.round(alpha * 100)}%, transparent)`;
  };
}
