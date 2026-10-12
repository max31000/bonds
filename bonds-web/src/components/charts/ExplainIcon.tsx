import type { ReactNode } from 'react';
import { ActionIcon, Popover } from '@mantine/core';

interface ChartExplainIconProps {
  /** Название показателя/графика — для aria-label. */
  title: string;
  /** Текст объяснения по-русски (plan/18 часть C). */
  explanation: ReactNode;
  'data-testid'?: string;
}

/**
 * «?» с объяснением рядом с показателем вне графика (KPI, колонка таблицы). У графиков то же самое
 * даёт `ChartFrame` кита (`explanation`); здесь — тот же вид (`mvv-chart-help`) для мест без
 * ChartFrame: в ките нет отдельного экспорта этой иконки.
 */
export function ChartExplainIcon({ title, explanation, ...rest }: ChartExplainIconProps) {
  return (
    <Popover width={300} position="bottom-start" withArrow>
      <Popover.Target>
        <ActionIcon
          variant="subtle"
          color="gray"
          size="xs"
          radius="xl"
          data-testid={rest['data-testid'] ?? 'chart-explain-icon'}
          aria-label={`Пояснение: ${title}`}
        >
          <span className="mvv-chart-help" aria-hidden="true">
            ?
          </span>
        </ActionIcon>
      </Popover.Target>
      <Popover.Dropdown>
        <div className="mvv-chart-explanation">{explanation}</div>
      </Popover.Dropdown>
    </Popover>
  );
}
