import type { CurrencyRatePoint } from '../api/types';

/**
 * Мини-график курса из `rateHistory` (рубли за 1 единицу): чистый SVG, без библиотек. Цвет — по тренду
 * (рост курса — зелёный, падение — красный); меньше двух точек — ничего не рисуем.
 */
export function RateSparkline({ points, label, width = 96, height = 28 }: { points: CurrencyRatePoint[]; label: string; width?: number; height?: number }) {
  if (points.length < 2) return null;
  const values = points.map((p) => p.closeRub);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const pad = 2;
  const coords = values
    .map((v, i) => {
      const x = pad + (i / (values.length - 1)) * (width - pad * 2);
      const y = pad + (1 - (v - min) / span) * (height - pad * 2);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
  const trend = values[values.length - 1] - values[0];
  const cls = Math.abs(trend) < 1e-9 ? '' : trend > 0 ? 'bonds-positive' : 'bonds-negative';
  return (
    <svg role="img" aria-label={label} width={width} height={height} viewBox={`0 0 ${width} ${height}`} className={cls} data-testid="rate-sparkline">
      <polyline points={coords} fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}
