import { useMediaQuery } from '@mantine/hooks';

/**
 * Компактные размеры графика на узких экранах (plan/21 часть C.4) — на телефоне полноразмерный
 * график (высота 300–360, до 8 подписей оси X) не помещается и превращается в кашу из подписей.
 * Единая точка правды для всех виджетов Recharts на Analytics/Cashflow/PositionDetail/Dashboard,
 * чтобы мобильные размеры не разъезжались по страницам.
 */
export interface ResponsiveChartSize {
  /** Высота ResponsiveContainer — компактная (240) на мобиле, иначе `desktopHeight`. */
  height: number;
  /** Максимум подписей оси X для pickAxisTicks — меньше на мобиле, чтобы подписи не наезжали друг на друга. */
  maxTicks: number;
  /** True на экранах уже `sm` (< 48em) — можно использовать и для прочих мобильных адаптаций внутри графика. */
  isCompact: boolean;
}

const COMPACT_HEIGHT = 240;
const COMPACT_MAX_TICKS = 4;
const DEFAULT_MAX_TICKS = 8;

/** Возвращает компактные размеры графика, когда экран уже `sm` (`max-width: 48em`, синхронизировано с AppShell navbar breakpoint). */
export function useResponsiveChartSize(desktopHeight: number): ResponsiveChartSize {
  const isCompact = useMediaQuery('(max-width: 48em)') ?? false;
  return {
    height: isCompact ? COMPACT_HEIGHT : desktopHeight,
    maxTicks: isCompact ? COMPACT_MAX_TICKS : DEFAULT_MAX_TICKS,
    isCompact,
  };
}
