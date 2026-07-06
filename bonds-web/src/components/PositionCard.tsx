import { useState } from 'react';
import { Paper, Group, Stack, Text, Badge, Collapse, UnstyledButton } from '@mantine/core';
import type { PositionRow } from '../api/types';
import { LiveMarketValueCell } from './positionsShared';
import { effectiveYield, COUPON_TYPE_LABEL } from '../utils/positionsDisplay';
import { formatRub, formatDaysUntil, formatPercent, formatNumber, formatBp } from '../utils/format';

/**
 * Карточка позиции для мобильного экрана (plan/21 часть C.2) — замена строки таблицы на
 * `< 768px`, где 10+ колонок нечитаемы даже с горизонтальным скроллом. Показывает сразу
 * имя/бейджи, стоимость, доходность и P&L (то, что план требует видимым без раскрытия);
 * остальные метрики (дюрация, G-спред, купоны, срок) — в `Collapse` по тапу на карточку.
 * <p>
 * Переиспользует ту же логику расчёта/форматирования, что и десктопная таблица
 * (Positions.tsx) через components/positionsShared.tsx — числа не должны разъезжаться между
 * версткой для десктопа и мобилы.
 */
export function PositionCard({ row, onClick }: { row: PositionRow; onClick: () => void }) {
  const [expanded, setExpanded] = useState(false);
  const yieldValue = effectiveYield(row);
  const isCurrentYield = row.isFloater || row.isIndexed;
  const pnlColor = row.unrealizedPnlRub === null ? undefined : row.unrealizedPnlRub >= 0 ? 'green' : 'red';

  return (
    <Paper withBorder p="sm" radius="md" data-testid={`position-card-${row.positionId}`}>
      <UnstyledButton onClick={onClick} w="100%" data-testid={`position-card-open-${row.positionId}`}>
        <Stack gap={4}>
          <Group justify="space-between" wrap="nowrap" align="flex-start">
            <Text fw={600} size="sm" lineClamp={2}>
              {row.name ?? row.issuer ?? row.isin ?? '—'}
            </Text>
            {row.unrealizedPnlRub !== null && (
              <Text c={pnlColor} fw={600} size="sm" span aria-hidden="true">
                {row.unrealizedPnlRub >= 0 ? '▲' : '▼'}
              </Text>
            )}
          </Group>

          <Group gap={4} wrap="wrap">
            <Badge size="sm" color="gray" variant="outline">
              {row.sector ?? COUPON_TYPE_LABEL[row.couponType]}
            </Badge>
            {row.isFloater && (
              <Badge size="sm" color="blue" variant="light">
                плавающая
              </Badge>
            )}
            {row.isIndexed && (
              <Badge size="sm" color="teal" variant="light">
                индексируемая
              </Badge>
            )}
            {row.calculatedToOffer && (
              <Badge size="sm" color="orange" variant="light">
                к оферте
              </Badge>
            )}
            {row.dataIncomplete && (
              <Badge size="sm" color="red" variant="light">
                данные неполные
              </Badge>
            )}
            {row.costBasisIncomplete && (
              <Badge size="sm" color="gray" variant="light">
                журнал неполон
              </Badge>
            )}
            {row.isOutOfScopeCurrency && (
              <Badge size="sm" color="grape" variant="light">
                валютная / вне скоупа
              </Badge>
            )}
          </Group>

          <Group justify="space-between" mt={4}>
            <Stack gap={0}>
              <Text size="xs" c="dimmed">
                Стоимость
              </Text>
              <LiveMarketValueCell staticValueRub={row.marketValueRub} positionId={row.positionId} />
            </Stack>
            <Stack gap={0} align="flex-end">
              <Text size="xs" c="dimmed">
                Доходность
              </Text>
              <Text>
                {formatPercent(yieldValue)}
                {isCurrentYield && (
                  <Text span size="xs" c="dimmed">
                    {' '}
                    (тек.)
                  </Text>
                )}
              </Text>
            </Stack>
            <Stack gap={0} align="flex-end">
              <Text size="xs" c="dimmed">
                P&amp;L
              </Text>
              <Text c={pnlColor} fw={500}>
                {formatRub(row.unrealizedPnlRub)}
              </Text>
              <Text c={pnlColor} size="xs">
                {formatPercent(row.unrealizedPnlPercent)}
              </Text>
            </Stack>
          </Group>
        </Stack>
      </UnstyledButton>

      <UnstyledButton
        mt="xs"
        onClick={(e) => {
          e.stopPropagation();
          setExpanded((v) => !v);
        }}
        data-testid={`position-card-toggle-${row.positionId}`}
      >
        <Text size="xs" c="violet">
          {expanded ? 'Скрыть детали ▲' : 'Подробнее ▼'}
        </Text>
      </UnstyledButton>

      <Collapse expanded={expanded}>
        <Stack gap={4} mt="xs" data-testid={`position-card-details-${row.positionId}`}>
          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              Кол-во
            </Text>
            <Text size="xs">{row.quantity}</Text>
          </Group>
          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              Ср. цена входа
            </Text>
            <Text size="xs">{formatRub(row.averageCostRub)}</Text>
          </Group>
          {row.accruedTotalRub > 0 && (
            <Group justify="space-between">
              <Text size="xs" c="dimmed">
                в т.ч. НКД
              </Text>
              <Text size="xs" data-testid={`accrued-caption-card-${row.positionId}`}>
                {formatRub(row.accruedTotalRub)}
              </Text>
            </Group>
          )}
          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              Купоны получено
            </Text>
            <Text size="xs">{formatRub(row.couponsReceivedRub)}</Text>
          </Group>
          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              Дюрация, лет
            </Text>
            <Text size="xs">{formatNumber(row.modifiedDuration)}</Text>
          </Group>
          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              G-спред, б.п.
            </Text>
            <Text size="xs">{formatBp(row.gSpread)}</Text>
          </Group>
          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              До погашения/оферты
            </Text>
            <Text size="xs">
              {formatDaysUntil(row.calculatedToOffer ? row.horizonDate : row.maturityDate)}
              {row.calculatedToOffer ? ' (оферта)' : ''}
            </Text>
          </Group>
        </Stack>
      </Collapse>
    </Paper>
  );
}
