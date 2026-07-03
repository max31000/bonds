import { Paper, Text } from '@mantine/core';

/** Одна строка тултипа: подпись + отформатированное значение (полное, не компактное). */
export interface ChartTooltipRow {
  label: string;
  value: string;
  /** Цвет текста строки (например, зелёный/красный для знака изменения). */
  color?: string;
}

interface ChartTooltipProps {
  /** Заголовок тултипа (обычно дата/имя категории/точки). */
  title?: string;
  rows: ChartTooltipRow[];
}

/**
 * Единый визуальный тултип графиков chart-kit (plan/18 часть A) — заменяет пять самописных
 * `content={...}` в Analytics.tsx/Cashflow.tsx одинаковой Paper+Text вёрсткой. Форматирование
 * значений остаётся на вызывающей стороне (formatRub/formatPercent/...) — тултип лишь раскладывает
 * готовые строки.
 */
export function ChartTooltip({ title, rows }: ChartTooltipProps) {
  if (rows.length === 0 && !title) return null;
  return (
    <Paper withBorder p="xs" radius="sm" shadow="sm">
      {title && (
        <Text size="xs" fw={600} mb={rows.length > 0 ? 4 : 0}>
          {title}
        </Text>
      )}
      {rows.map((row, idx) => (
        <Text key={`${row.label}-${idx}`} size="xs" c={row.color}>
          {row.label}: {row.value}
        </Text>
      ))}
    </Paper>
  );
}
