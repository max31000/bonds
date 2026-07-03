import type { ReactNode } from 'react';
import { ActionIcon, Group, Paper, Popover, Text } from '@mantine/core';

interface ChartCardProps {
  title: string;
  /** Текст объяснения графика по-русски (plan/18 часть C) — открывается по клику на «?». */
  explanation?: ReactNode;
  /** Слот под контролы виджета (переключатель периода/разреза и т.п.), рядом с заголовком. */
  controls?: ReactNode;
  /** Доп. подпись справа от заголовка (например, дата актуальности кривой). */
  headerExtra?: ReactNode;
  children: ReactNode;
  'data-testid'?: string;
}

/**
 * Paper-обёртка виджета графика (plan/18 часть A.5) — заголовок, слот под контролы и иконка «?»
 * (Popover) с объяснением, что показывает график и как его читать. Общая для Analytics/Cashflow/
 * Dashboard, чтобы у всех виджетов был одинаковый заголовок/отступы вместо самописных Group/Paper
 * в каждом компоненте.
 */
export function ChartCard({ title, explanation, controls, headerExtra, children, ...rest }: ChartCardProps) {
  return (
    <Paper withBorder p="md" radius="md" data-testid={rest['data-testid']}>
      <Group justify="space-between" mb="xs" wrap="wrap">
        <Group gap={6} wrap="nowrap">
          <Text fw={600}>{title}</Text>
          {explanation && (
            <Popover width={320} withArrow shadow="md" position="bottom-start">
              <Popover.Target>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="xs"
                  radius="xl"
                  data-testid={`${rest['data-testid'] ?? 'chart-card'}-explain-icon`}
                  aria-label={`Пояснение к графику «${title}»`}
                >
                  <Text size="xs" fw={700}>
                    ?
                  </Text>
                </ActionIcon>
              </Popover.Target>
              <Popover.Dropdown>
                <Text size="xs">{explanation}</Text>
              </Popover.Dropdown>
            </Popover>
          )}
        </Group>
        <Group gap="xs" wrap="wrap">
          {headerExtra}
          {controls}
        </Group>
      </Group>
      {children}
    </Paper>
  );
}
