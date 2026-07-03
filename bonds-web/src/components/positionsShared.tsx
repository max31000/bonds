import { useEffect, useRef, useState } from 'react';
import { Stack, Text, Group, Tooltip } from '@mantine/core';
import { useLiveStore } from '../store/useLiveStore';
import { formatRub, formatPercent, formatDateTime } from '../utils/format';

/**
 * Общий для десктопной таблицы (Positions.tsx) и мобильных карточек (PositionCard.tsx, plan/21
 * часть C.2) компонент. Хелперы-функции/константы (effectiveYield, COUPON_TYPE_LABEL, ...) — в
 * utils/positionsDisplay.ts, а не здесь: react-refresh/only-export-components запрещает файлу,
 * который экспортирует компонент, экспортировать что-то ещё.
 */

/**
 * Ячейка «Рыночная стоимость» с live-merge поверх статичного значения из GET /api/positions
 * (plan/16 часть B): пока не пришёл ни один тик — обычное значение из positions-стора; как
 * только приходит live-цена — подменяется на неё, с мягкой CSS-подсветкой при изменении и
 * пометкой «цены на HH:MM» для устаревшего (isStale) фолбэка на дневной снимок последнего синка.
 */
export function LiveMarketValueCell({ staticValueRub, positionId }: { staticValueRub: number; positionId: number }) {
  const live = useLiveStore((s) => s.positionsById[positionId]);
  const [isHighlighted, setIsHighlighted] = useState(false);
  const prevValueRef = useRef<number | null>(null);

  const displayValue = live?.marketValueRub ?? staticValueRub;

  useEffect(() => {
    if (prevValueRef.current !== null && prevValueRef.current !== displayValue) {
      setIsHighlighted(true);
      const timeout = setTimeout(() => setIsHighlighted(false), 1000);
      return () => clearTimeout(timeout);
    }
    prevValueRef.current = displayValue;
    return undefined;
  }, [displayValue]);

  useEffect(() => {
    prevValueRef.current = displayValue;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Stack gap={0} data-testid={`live-market-value-${positionId}`}>
      <Text
        fw={live ? 600 : 400}
        style={{
          transition: 'color 300ms ease, background-color 300ms ease',
          backgroundColor: isHighlighted ? 'var(--mantine-color-yellow-1)' : 'transparent',
        }}
      >
        {formatRub(displayValue)}
      </Text>
      {live && (
        <Group gap={4} wrap="nowrap">
          {live.changeDayPercent !== null && (
            <Text size="xs" c={live.changeDayPercent >= 0 ? 'green' : 'red'}>
              {live.changeDayPercent >= 0 ? '+' : ''}
              {formatPercent(live.changeDayPercent)}
            </Text>
          )}
          {live.isStale && (
            <Tooltip label={`Цены на ${formatDateTime(live.asOfUtc)} — новых тиков ещё не было`} withArrow>
              <Text size="xs" c="dimmed" data-testid={`live-stale-${positionId}`}>
                (цены на {formatDateTime(live.asOfUtc)})
              </Text>
            </Tooltip>
          )}
        </Group>
      )}
    </Stack>
  );
}
