import { Badge, Group, Stack, Table, Text } from '@mantine/core';
import { EmptyState, ErrorState, Section } from '@mvv42/kit';
import { useWidgetData } from '../hooks/useWidgetData';
import { fetchCash } from '../api/cash';
import type { BrokerCashSummary, CashMovementRow } from '../api/types';
import { formatDate, formatMoney, formatRub } from '../utils/format';
import { CASH_BROKER_LABEL, CASH_DATA_INCOMPLETE, CASH_MOVEMENT_LABEL, CASH_NOTE } from './cashCopy';

/** Сумма движения в своей валюте со знаком брокера; не рубль — с оценкой в рублях. */
function movementAmount(m: CashMovementRow): string {
  const own = formatMoney(m.amount, m.currency, 2);
  return m.currency === 'RUB' || m.amountRub === null ? own : `${own} (≈ ${formatRub(m.amountRub)})`;
}

function BrokerBalances({ summary }: { summary: BrokerCashSummary }) {
  const label = CASH_BROKER_LABEL[summary.broker] ?? summary.broker;
  return (
    <Stack gap="xs" data-testid={`cash-broker-${summary.broker}`}>
      <Group gap="xs" px="md" pt="sm" justify="space-between">
        <Text fw={600}>{label}</Text>
        {summary.cashRubOnly !== null && (
          <Text size="sm" c="dimmed" data-testid={`cash-rub-only-${summary.broker}`}>
            Свободных рублей: {formatRub(summary.cashRubOnly)}
          </Text>
        )}
      </Group>
      {summary.balances.length === 0 ? (
        <Text size="sm" c="dimmed" px="md" pb="sm">
          Остатков нет.
        </Text>
      ) : (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Валюта</Table.Th>
              <Table.Th ta="right">Сумма</Table.Th>
              <Table.Th ta="right">В рублях</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {summary.balances.map((b) => (
              <Table.Tr key={b.currency}>
                <Table.Td>{b.currency}</Table.Td>
                <Table.Td ta="right">{formatMoney(b.amount, b.currency, 2)}</Table.Td>
                <Table.Td ta="right">{b.amountRub === null ? '—' : formatRub(b.amountRub)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {summary.dataIncomplete && (
        <Text size="xs" c="dimmed" px="md" pb="sm" data-testid={`cash-incomplete-${summary.broker}`}>
          {CASH_DATA_INCOMPLETE}
        </Text>
      )}
    </Stack>
  );
}

/**
 * Блок «Деньги на счетах» на «Обзоре» (задача 61): свободные деньги по брокерам и валютам (рубли без валюты — отдельной строкой) и последние
 * пополнения/выводы. Грузится независимо от остальных виджетов (`GET /api/cash`): отказ даёт ошибку только этого блока.
 */
export function CashOverviewSection() {
  const { data, isLoading, error } = useWidgetData(fetchCash, []);

  const movements = (data?.brokers ?? [])
    .flatMap((b) => b.recentMovements.map((m) => ({ ...m, broker: b.broker })))
    .sort((a, b) => b.date.localeCompare(a.date))
    .slice(0, 10);

  return (
    <Section title="Деньги на счетах" padding="none" data-testid="widget-cash">
      {error && <ErrorState compact error={error} data-testid="widget-cash-error" />}
      {!isLoading && !error && data && data.brokers.length === 0 && (
        <EmptyState
          compact
          title="Данных о деньгах нет"
          description="Остатки появятся после синка (кнопка «Обновить данные»)."
          data-testid="widget-cash-empty"
        />
      )}
      {!error && data && data.brokers.map((b) => <BrokerBalances key={b.broker} summary={b} />)}
      {!error && data && data.brokers.length > 0 && (
        <Stack gap="xs" data-testid="cash-movements">
          <Text fw={600} px="md" pt="sm">
            Последние пополнения и выводы
          </Text>
          {movements.length === 0 ? (
            <Text size="sm" c="dimmed" px="md" pb="sm" data-testid="cash-movements-empty">
              Пополнений и выводов пока нет.
            </Text>
          ) : (
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Дата</Table.Th>
                  <Table.Th>Счёт</Table.Th>
                  <Table.Th>Тип</Table.Th>
                  <Table.Th ta="right">Сумма</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {movements.map((m, idx) => (
                  <Table.Tr key={`${m.broker}-${m.date}-${idx}`} data-testid="cash-movement-row">
                    <Table.Td>{formatDate(m.date)}</Table.Td>
                    <Table.Td c="dimmed">{CASH_BROKER_LABEL[m.broker] ?? m.broker}</Table.Td>
                    <Table.Td>
                      <Badge size="sm" variant="light" color={m.type === 'deposit' ? 'green' : 'gray'}>
                        {CASH_MOVEMENT_LABEL[m.type] ?? m.type}
                      </Badge>
                    </Table.Td>
                    <Table.Td ta="right">{movementAmount(m)}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
          <Text size="xs" c="dimmed" px="md" pb="sm">
            {CASH_NOTE}
          </Text>
        </Stack>
      )}
    </Section>
  );
}
