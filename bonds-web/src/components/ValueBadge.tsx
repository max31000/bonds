import { Badge, Stack, Text, Tooltip } from '@mantine/core';
import type { ValueVerdict } from '../api/types';
import { formatBp } from '../utils/format';

/** Подпись/цвет оси Value. Цвет «дёшево» НЕ красный: дешевизна — информация о цене, а не риск (мотивация цикла 46–47). */
const VALUE_LABEL: Record<ValueVerdict, string> = {
  Cheap: 'дёшево',
  Fair: 'справедливо',
  Rich: 'дорого',
  InsufficientData: 'мало данных',
};

const VALUE_COLOR: Record<ValueVerdict, string> = {
  Cheap: 'teal',
  Fair: 'gray',
  Rich: 'orange',
  InsufficientData: 'gray',
};

const VALUE_HINT: Record<ValueVerdict, string> = {
  Cheap: 'Спред заметно выше медианы корзины: рынок платит больше за такой же срок и сектор (либо закладывает риск).',
  Fair: 'Спред в пределах порога от медианы корзины.',
  Rich: 'Спред заметно ниже медианы корзины: бумага дороже аналогов.',
  InsufficientData: 'Сопоставимых бумаг в корзине мало (оценка по всему рынку) или у бумаги нет G-спреда: вердикт «дёшево/дорого» не выдаётся.',
};

/** Строки тултипа Value: «G-спред X б.п. против медианы корзины Y б.п. (порог ±Z б.п.)» — все числа ДОЛИ, б.п. рисует `formatBp`. */
function tooltipLines(
  verdict: ValueVerdict,
  gSpreadFraction: number | null | undefined,
  medianFraction: number | null | undefined,
  thresholdFraction: number | null | undefined,
): string[] {
  const lines: string[] = [];
  if (gSpreadFraction != null && medianFraction != null) {
    const threshold = thresholdFraction != null ? ` (порог ±${formatBp(thresholdFraction)})` : '';
    lines.push(`G-спред ${formatBp(gSpreadFraction)} против медианы корзины ${formatBp(medianFraction)}${threshold}`);
  }
  lines.push(VALUE_HINT[verdict]);
  lines.push('Это ось цены, она не входит в балл надёжности.');
  return lines;
}

/**
 * Бейдж оси Value (задача 47): Cheap / Fair / Rich / «мало данных» — НЕЗАВИСИМО от светофора надёжности (Risk):
 * «дешёвая и красная» и «дешёвая и зелёная» — разные бумаги. Рядом со светофором везде, где он показан.
 * Все числа — ДОЛИ (0.003 = 30 б.п.).
 */
export function ValueBadge({
  verdict,
  gSpreadFraction,
  medianFraction,
  thresholdFraction,
  testIdSuffix,
}: {
  verdict: ValueVerdict;
  gSpreadFraction?: number | null;
  medianFraction?: number | null;
  thresholdFraction?: number | null;
  testIdSuffix: string;
}) {
  const lines = tooltipLines(verdict, gSpreadFraction, medianFraction, thresholdFraction);
  return (
    <Tooltip
      multiline
      w={280}
      withArrow
      label={
        <Stack gap={2} data-testid={`value-tooltip-${testIdSuffix}`}>
          {lines.map((line) => (
            <Text size="xs" key={line}>
              {line}
            </Text>
          ))}
        </Stack>
      }
    >
      <Badge
        size="xs"
        color={VALUE_COLOR[verdict]}
        variant={verdict === 'InsufficientData' || verdict === 'Fair' ? 'outline' : 'light'}
        data-testid={`value-badge-${testIdSuffix}`}
        data-verdict={verdict}
      >
        {VALUE_LABEL[verdict]}
      </Badge>
    </Tooltip>
  );
}
