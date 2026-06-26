import type { ScatterPoint } from '../api/types';

/**
 * T-7/L-1: и ось X scatter, и G-спред должны мерить «срок» одним измерителем — дюрацией Маколея.
 * Раньше точки наносились по модифицированной дюрации, а G-спред считался по Маколею, поэтому
 * визуальное «над/под кривой» не совпадало со знаком G-спреда. Здесь точки строятся по
 * macaulayDuration (нейтральный ключ durationYears), кривая — по своему сроку (termYears).
 * Вынесено в отдельный модуль ради юнит-теста (recharts не рендерит ось в jsdom) и чтобы не
 * нарушать react-refresh (страница экспортирует только компонент).
 */
export function buildScatterChartData(scatter: {
  points: ScatterPoint[];
  curve: { termYears: number; yield: number }[];
}) {
  const points = scatter.points
    .filter((p) => p.effectiveYield != null)
    .map((p) => ({
      ...p,
      durationYears: p.macaulayDuration,
      yieldFraction: p.effectiveYield,
      yieldPercent: (p.effectiveYield ?? 0) * 100,
    }));

  const durations = points.map((p) => p.durationYears).filter(Boolean) as number[];
  const dMin = durations.length > 0 ? Math.min(...durations) : 0;
  const dMax = durations.length > 0 ? Math.max(...durations) : 5;
  const xDomainMin = Math.max(0, dMin - 0.5);
  const xDomainMax = dMax + 0.5;

  const toCurvePoint = (c: { termYears: number; yield: number }) => ({
    durationYears: c.termYears,
    yieldPercent: c.yield * 100,
    yieldFraction: c.yield,
  });

  let curve = scatter.curve
    .filter((c) => c.termYears >= xDomainMin && c.termYears <= xDomainMax)
    .sort((a, b) => a.termYears - b.termYears)
    .map(toCurvePoint);

  // If curve is empty after clipping but scatter.curve is not, include all curve
  if (curve.length === 0 && scatter.curve.length > 0) {
    curve = scatter.curve.slice().sort((a, b) => a.termYears - b.termYears).map(toCurvePoint);
  }

  return { points, curve, xDomainMin, xDomainMax };
}
