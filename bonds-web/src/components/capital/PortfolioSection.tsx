import { useState } from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import { Metric, MetricGrid, Section } from '@mvv42/kit';
import type { BasketBatchItem, CapitalPortfolio } from '../../api/types';
import type { BasketsSaveResult } from '../../store/useBasketsStore';
import { formatPortfolioMoney, formatWeight, targetsSumOk } from '../../utils/baskets';
import { formatDateTime } from '../../utils/format';
import { BasketCard } from './BasketCard';
import { BasketTargetsForm } from './BasketTargetsForm';

const TITLES: Record<string, { title: string; description: string }> = {
  rub: { title: 'Рублёвый портфель', description: 'T-Invest: облигации, фонды, рубли и валюта. Оценка в рублях.' },
  usd: { title: 'Долларовый портфель', description: 'Freedom: акции и ETF. Оценка в долларах по курсам bonds.' },
};

/** Один из двух самостоятельных портфелей: итоги, корзины с отклонениями, редактирование целей, сброс к стартовым. */
export function PortfolioSection({
  portfolio,
  onSave,
  onReset,
}: {
  portfolio: CapitalPortfolio;
  onSave: (items: BasketBatchItem[]) => Promise<BasketsSaveResult>;
  onReset: () => Promise<BasketsSaveResult>;
}) {
  const [editing, setEditing] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);
  const [resetError, setResetError] = useState<string | null>(null);
  const meta = TITLES[portfolio.portfolio] ?? { title: portfolio.portfolio, description: '' };
  const sumOk = targetsSumOk(portfolio.baskets.map((b) => (b.isSystem ? null : b.targetWeight)));

  const handleSave = async (items: BasketBatchItem[]) => {
    const result = await onSave(items);
    if (result.ok) setEditing(false);
    return result;
  };

  const handleReset = async () => {
    setResetError(null);
    const result = await onReset();
    setConfirmReset(false);
    if (!result.ok) setResetError(result.error);
    else setEditing(false);
  };

  return (
    <Section
      title={meta.title}
      description={meta.description}
      padding="none"
      data-testid={`capital-portfolio-${portfolio.portfolio}`}
      actions={
        <Group gap="xs">
          {!editing && (
            <Button size="xs" variant="light" onClick={() => setEditing(true)} data-testid={`targets-edit-${portfolio.portfolio}`}>
              Изменить цели
            </Button>
          )}
          {confirmReset ? (
            <>
              <Button size="xs" color="red" onClick={() => void handleReset()} data-testid={`reset-confirm-${portfolio.portfolio}`}>
                Подтвердить сброс
              </Button>
              <Button size="xs" variant="default" onClick={() => setConfirmReset(false)}>
                Отмена
              </Button>
            </>
          ) : (
            <Button size="xs" variant="default" onClick={() => setConfirmReset(true)} data-testid={`reset-${portfolio.portfolio}`}>
              Сбросить к стартовым
            </Button>
          )}
        </Group>
      }
    >
      <Stack gap="sm">
        <div style={{ padding: 'var(--mvv-sp-4)' }}>
          <MetricGrid>
            <Metric label="Стоимость портфеля" value={formatPortfolioMoney(portfolio.totalValue, portfolio.currency)} data-testid={`capital-total-${portfolio.portfolio}`} />
            <Metric
              label="База для долей"
              value={formatPortfolioMoney(portfolio.baseValue, portfolio.currency)}
              hint="без корзин «вне целей»"
              data-testid={`capital-base-${portfolio.portfolio}`}
            />
            <Metric label="Свободные деньги" value={formatPortfolioMoney(portfolio.freeCash, portfolio.currency)} data-testid={`capital-cash-${portfolio.portfolio}`} />
            <Metric
              label="Сумма целей"
              value={formatWeight(portfolio.targetsSum)}
              hint={sumOk ? undefined : 'цели не сходятся к 100%'}
              data-testid={`capital-targets-sum-${portfolio.portfolio}`}
            />
          </MetricGrid>
        </div>
        {!portfolio.hasAccount && (
          <Text size="sm" c="dimmed" px="md" data-testid={`capital-no-account-${portfolio.portfolio}`}>
            Счёта брокера пока нет: корзины и цели сохранены, позиции появятся после синка.
          </Text>
        )}
        {portfolio.dataIncomplete && (
          <Text size="xs" c="dimmed" px="md" data-testid={`capital-incomplete-${portfolio.portfolio}`}>
            Не для всех бумаг есть цена или курс — стоимость занижена, отклонения приблизительны.
          </Text>
        )}
        {!sumOk && !editing && (
          <Text size="sm" c="red" px="md" data-testid={`capital-sum-warning-${portfolio.portfolio}`}>
            Сумма целей {formatWeight(portfolio.targetsSum)} вместо 100% — поправьте цели.
          </Text>
        )}
        {resetError && (
          <Text size="sm" c="red" px="md">
            {resetError}
          </Text>
        )}
        {editing && <BasketTargetsForm portfolio={portfolio} onSave={handleSave} onCancel={() => setEditing(false)} />}
        <Stack gap={0}>
          {portfolio.baskets.map((b) => (
            <BasketCard key={b.code} basket={b} currency={portfolio.currency} />
          ))}
        </Stack>
        {portfolio.syncedAt && (
          <Text size="xs" c="dimmed" px="md" pb="sm">
            Данные брокера на {formatDateTime(portfolio.syncedAt)}
          </Text>
        )}
      </Stack>
    </Section>
  );
}
