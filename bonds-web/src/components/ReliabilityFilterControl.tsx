import { Group, SegmentedControl, Text } from '@mantine/core';
import type { ReliabilityFilterValue } from '../utils/reliabilityFilter';

const OPTIONS: { label: string; value: ReliabilityFilterValue }[] = [
  { label: 'все', value: 'all' },
  { label: '🟢', value: 'green' },
  { label: '🟡', value: 'yellow' },
];

/**
 * Задача 38 часть C.3 — переключатель «не хуже: 🟢/🟡/все», общий для панели кандидатов блока 1
 * (Recommendations) и FilterPanel скринера (клиентская/серверная фильтрация — разная у вызывающего
 * кода, компонент только рендерит выбор). См. `utils/reliabilityFilter.ts` для семантики значений
 * и чистой функции фильтрации (вынесена в отдельный util-файл, чтобы не смешивать экспорт
 * компонента с экспортом функции/типа в одном файле — react-refresh/only-export-components).
 */
export function ReliabilityFilterControl({
  value,
  onChange,
  testId,
}: {
  value: ReliabilityFilterValue;
  onChange: (value: ReliabilityFilterValue) => void;
  testId: string;
}) {
  return (
    <Group gap={6} align="center" wrap="nowrap">
      <Text size="xs" c="dimmed">
        Надёжность не хуже:
      </Text>
      <SegmentedControl
        size="xs"
        value={value}
        onChange={(v) => onChange(v as ReliabilityFilterValue)}
        data={OPTIONS}
        data-testid={testId}
      />
    </Group>
  );
}
