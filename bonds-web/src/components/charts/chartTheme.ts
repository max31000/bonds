/**
 * Общие соглашения chart-kit (plan/18 часть A) — единая палитра, отступы и сетка для всех
 * графиков Recharts на экранах Analytics/Cashflow/Dashboard. Не обёртка над Recharts, а набор
 * констант, чтобы виджеты выглядели единообразно и подписи осей не наезжали на легенду.
 */

/** Палитра секторов/категорий (композиция, scatter) — вынесена из Analytics.tsx (было PIE_COLORS). */
export const CHART_COLORS = [
  'var(--mantine-color-violet-6)',
  'var(--mantine-color-teal-6)',
  'var(--mantine-color-orange-6)',
  'var(--mantine-color-blue-6)',
  'var(--mantine-color-red-5)',
  'var(--mantine-color-yellow-6)',
  'var(--mantine-color-grape-6)',
  'var(--mantine-color-cyan-6)',
];

/** Общая сетка графика — закрепляем уже принятое соглашение (было разбросано по виджетам). */
export const CHART_GRID_PROPS = {
  strokeDasharray: '3 3',
} as const;

/**
 * Отступы графика с местом под подпись оси X снизу, без конфликта с легендой сверху
 * (легенда — `verticalAlign="top"`, подпись оси X рисуется через отдельный <Text> под графиком
 * вместо `label` на `<XAxis>`, который в Recharts перекрывается легендой при недостатке высоты).
 */
export const CHART_MARGIN = { top: 8, right: 16, bottom: 8, left: 4 };

/** Высота графиков «средних» виджетов аналитики (scatter/xirr/rate-scenario/trajectory). */
export const CHART_HEIGHT = 320;

/** Высота компактных виджетов дашборда. */
export const CHART_HEIGHT_COMPACT = 220;

/** Общие пропсы легенды — сверху, чтобы не спорить с подписью оси X снизу. */
export const CHART_LEGEND_PROPS = {
  verticalAlign: 'top' as const,
  height: 32,
};
