import { Badge, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import type { CapitalBasket } from '../../api/types';
import {
  BASKET_STATE_COLOR,
  BASKET_STATE_LABEL,
  formatDrift,
  formatPortfolioMoney,
  formatWeight,
} from '../../utils/baskets';
import { BasketBar } from './BasketBar';

function Figure({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div>
      <div className="bonds-eyebrow">{label}</div>
      <Text fw={600} data-testid={testId}>
        {value}
      </Text>
    </div>
  );
}

/** «Не хватает до цели» / «Избыток» по знаку `deficitToTarget` (валюта портфеля). */
function deficitText(deficit: number | null, currency: string): string {
  if (deficit === null) return '—';
  if (deficit === 0) return 'на цели';
  return deficit > 0
    ? `не хватает ${formatPortfolioMoney(deficit, currency)}`
    : `избыток ${formatPortfolioMoney(-deficit, currency)}`;
}

/** Корзина или системная строка: цель, факт, отклонение, полоса с допуском, состав. Только показ (редактирование — `BasketTargetsForm`). */
export function BasketCard({ basket, currency }: { basket: CapitalBasket; currency: string }) {
  const noTarget = basket.target === null;
  const actual = noTarget ? basket.shareOfTotal : basket.weight;

  return (
    <Stack gap="xs" p="md" className="bonds-basket-card" data-testid={`basket-${basket.code}`}>
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Group gap="xs" wrap="wrap">
          <Text fw={600}>{basket.title}</Text>
          <Text size="xs" c="dimmed">
            {basket.isSystem ? 'системная' : basket.code}
          </Text>
        </Group>
        <Badge variant="light" color={BASKET_STATE_COLOR[basket.state]} data-testid={`basket-state-${basket.code}`}>
          {BASKET_STATE_LABEL[basket.state]}
        </Badge>
      </Group>

      <SimpleGrid cols={{ base: 2, sm: 5 }} spacing="sm">
        <Figure label="Цель" value={formatWeight(basket.target)} testId={`basket-target-${basket.code}`} />
        <Figure
          label={noTarget ? 'Доля портфеля' : 'Факт'}
          value={formatWeight(actual)}
          testId={`basket-weight-${basket.code}`}
        />
        <Figure label="Отклонение" value={formatDrift(basket.drift)} testId={`basket-drift-${basket.code}`} />
        <Figure label="Стоимость" value={formatPortfolioMoney(basket.value, currency)} />
        <Figure label="До цели" value={deficitText(basket.deficitToTarget, currency)} testId={`basket-deficit-${basket.code}`} />
      </SimpleGrid>

      {(!basket.isSystem || basket.code !== 'cash') && <BasketBar basket={basket} />}
      {!noTarget && basket.band !== null && basket.hardBand !== null && (
        <Text size="xs" c="dimmed">
          Допуск ±{formatWeight(basket.band)}, жёсткий ±{formatWeight(basket.hardBand)}
        </Text>
      )}

      {basket.members.length > 0 && (
        <Stack gap={2} data-testid={`basket-members-${basket.code}`}>
          {basket.members.map((m) => (
            <Group key={m.id} justify="space-between" gap="xs" wrap="nowrap">
              <Text size="sm">
                {m.matchRule === 'asset_class' ? `все бумаги класса «${m.instrumentKey}»` : m.instrumentKey}
              </Text>
              <Text size="xs" c="dimmed">
                {m.matchRule === 'instrument' && m.value != null ? formatPortfolioMoney(m.value, currency) : null}
                {m.subWeight !== null && ` · цель внутри ${formatWeight(m.subWeight)}`}
                {m.actualSubShare != null && `, факт ${formatWeight(m.actualSubShare)}`}
              </Text>
            </Group>
          ))}
        </Stack>
      )}
      {basket.members.length === 0 && basket.positions.length > 0 && (
        <Stack gap={2} data-testid={`basket-positions-${basket.code}`}>
          {basket.positions.map((p) => (
            <Group key={`${p.broker}-${p.key}`} justify="space-between" gap="xs" wrap="nowrap">
              <Text size="sm">{p.name || p.key}</Text>
              <Text size="xs" c="dimmed">
                {formatPortfolioMoney(p.value, currency)}
              </Text>
            </Group>
          ))}
        </Stack>
      )}
    </Stack>
  );
}
