import { Badge, Tooltip } from '@mantine/core';
import { formatBp } from '../utils/format';
import type { RelativeValuePosition } from '../api/types';

const CONFIDENCE_LABEL: Record<string, string> = {
  High: 'высокая надёжность',
  Medium: 'средняя надёжность (весь сектор)',
  Low: 'низкая надёжность (весь рынок)',
};

/**
 * Задача 30 часть D — компактный RV-бейдж: «дорогая: −38 б.п. к корзине» (оранжевый) / «дешёвая:
 * +25 б.п.» (зелёный) / ничего при Fair (verdict в пределах порога — не заслуживает бейджа, план
 * часть D.1). Tooltip — корзина (сектор × срок), перцентиль, confidence, basedOnDays (план часть D.1:
 * "tooltip: корзина, перцентиль, confidence, basedOnDays"). Переиспользуется на карточках слабых
 * звеньев (Recommendations.tsx) и в компактном индикаторе таблицы позиций (Positions.tsx).
 */
export function RelativeValueBadge({ rv }: { rv: RelativeValuePosition }) {
  if (rv.verdict === 'Fair') return null;

  const isRich = rv.verdict === 'Rich';
  // deviationFraction уже несёт знак (отрицательное — Rich, положительное — Cheap) — formatBp
  // рисует его как есть (Math.round даёт "-38 б.п."/"25 б.п."), явный "+" добавляем только для Cheap.
  const label = isRich
    ? `дорогая: ${formatBp(rv.deviationFraction)}`
    : `дешёвая: +${formatBp(rv.deviationFraction)}`;

  const tooltipLabel = [
    `Корзина: ${rv.basket.sector}, ${rv.basket.durationBucket} (${rv.basket.count} бумаг)`,
    `Перцентиль в корзине: ${rv.percentile}`,
    `Надёжность оценки: ${CONFIDENCE_LABEL[rv.basket.confidence] ?? rv.basket.confidence}`,
    rv.basedOnDays > 0 ? `Оценка по ${rv.basedOnDays} дн. истории` : 'Оценка по последнему снимку рынка (истории пока мало)',
  ].join('. ');

  return (
    <Tooltip label={tooltipLabel} multiline w={280} withArrow>
      <Badge
        size="sm"
        color={isRich ? 'orange' : 'green'}
        variant="light"
        data-testid={`relative-value-badge-${rv.positionId}`}
      >
        {label}
      </Badge>
    </Tooltip>
  );
}

/**
 * Задача 30 часть D.3 — компактный индикатор для «Пометок» таблицы позиций: ↑дешёвая/↓дорогая,
 * без текста отклонения (места в таблице мало) — детали в tooltip (тот же текст, что RelativeValueBadge).
 * rv может отсутствовать (данные ещё грузятся лениво или запрос отказал) — тогда null, ничего не
 * рендерится (план часть D.3: "устойчивость к отказу").
 */
export function CompactRelativeValueIndicator({ rv }: { rv: RelativeValuePosition | undefined }) {
  if (!rv || rv.verdict === 'Fair') return null;

  const isRich = rv.verdict === 'Rich';
  const tooltipLabel = [
    isRich ? `Дорогая: ${formatBp(rv.deviationFraction)} к медиане корзины` : `Дешёвая: +${formatBp(rv.deviationFraction)} к медиане корзины`,
    `Корзина: ${rv.basket.sector}, ${rv.basket.durationBucket} (${rv.basket.count} бумаг)`,
    `Перцентиль в корзине: ${rv.percentile}`,
    `Надёжность оценки: ${CONFIDENCE_LABEL[rv.basket.confidence] ?? rv.basket.confidence}`,
    rv.basedOnDays > 0 ? `Оценка по ${rv.basedOnDays} дн. истории` : 'Оценка по последнему снимку рынка (истории пока мало)',
  ].join('. ');

  return (
    <Tooltip label={tooltipLabel} multiline w={280} withArrow>
      <Badge
        size="sm"
        color={isRich ? 'orange' : 'green'}
        variant="outline"
        data-testid={`relative-value-compact-${rv.positionId}`}
      >
        {isRich ? '↓ дорогая' : '↑ дешёвая'}
      </Badge>
    </Tooltip>
  );
}
