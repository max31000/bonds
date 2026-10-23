import { Badge, Group, Paper, Stack, Table, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { Section } from '@mvv42/kit';
import type { BrokerCashRow, BrokerHoldingsResponse, BrokerPositionRow } from '../api/types';
import { formatDateTime, formatMoney, formatPercent, formatRub } from '../utils/format';
import { signClass } from '../utils/incomeDisplay';
import { FREEDOM_ASSET_CLASS_LABEL, FREEDOM_DATA_INCOMPLETE } from './freedomCopy';

/** Количество бумаг: целое без дробной части, иначе до 4 знаков (дробные акции). */
function formatQuantity(q: number): string {
  return new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 4 }).format(q);
}

/** Знаков после запятой для цены: от 2 до 4 по факту значения (цена ETF бывает 24,4075). */
function priceDigits(value: number): number {
  const fraction = Math.abs(value).toFixed(4).split('.')[1]?.replace(/0+$/, '') ?? '';
  return Math.min(4, Math.max(2, fraction.length));
}

/** Цена за бумагу в валюте бумаги — с кодом валюты. */
function price(value: number, currency: string): string {
  return formatMoney(value, currency, priceDigits(value));
}

function pnlText(p: BrokerPositionRow): string {
  return `${p.unrealizedPnl > 0 ? '+' : ''}${formatMoney(p.unrealizedPnl, p.currency)}`;
}

function RubCell({ value }: { value: number | null }) {
  return value === null ? (
    <span className="bonds-note" data-testid="freedom-rub-missing">
      нет курса
    </span>
  ) : (
    <span className="mvv-num">{formatRub(value)}</span>
  );
}

function PositionCard({ p }: { p: BrokerPositionRow }) {
  return (
    <Paper withBorder p="sm" radius="md" data-testid={`freedom-card-${p.ticker}`}>
      <Stack gap={4}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Text fw={600} size="sm" lineClamp={2}>
            {p.ticker}
          </Text>
          <Badge size="sm" color="gray" variant="outline">
            {p.currency}
          </Badge>
        </Group>
        <Text size="xs" c="dimmed" lineClamp={2}>
          {p.name}
        </Text>
        <Text fw={700}>{formatMoney(p.marketValue, p.currency)}</Text>
        <Text size="xs" c="dimmed">
          {formatQuantity(p.quantity)} × {price(p.lastPrice, p.currency)} · ср. {price(p.avgPrice, p.currency)}
        </Text>
        <Text size="xs" className={signClass(p.unrealizedPnl)}>
          P&amp;L {pnlText(p)} ({formatPercent(p.unrealizedPnlFraction)})
        </Text>
        <Text size="xs" c="dimmed">
          В рублях: <RubCell value={p.marketValueRub} />
        </Text>
      </Stack>
    </Paper>
  );
}

function CashBlock({ cash }: { cash: BrokerCashRow[] }) {
  if (cash.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-testid="freedom-cash-empty">
        Свободных денег нет.
      </Text>
    );
  }
  return (
    <Group gap="lg" data-testid="freedom-cash">
      {cash.map((c) => (
        <div key={c.currency} data-testid={`freedom-cash-${c.currency}`}>
          <Text fw={600}>{formatMoney(c.amount, c.currency)}</Text>
          <Text size="xs" c="dimmed">
            {c.amountRub === null ? 'нет курса' : `≈ ${formatRub(c.amountRub)}`}
          </Text>
        </div>
      ))}
    </Group>
  );
}

function Totals({ h }: { h: BrokerHoldingsResponse }) {
  return (
    <Group gap="md" data-testid="freedom-totals">
      <Text fw={700}>Итого: {formatRub(h.totalValueRub)}</Text>
      {h.totalValueUsd !== null && <Text c="dimmed">≈ {formatMoney(h.totalValueUsd, 'USD', 0)}</Text>}
      {h.syncedAt && (
        <Text size="xs" c="dimmed">
          синк {formatDateTime(h.syncedAt)}
        </Text>
      )}
      {h.dataIncomplete && (
        <Text size="xs" c="orange" data-testid="freedom-incomplete">
          {FREEDOM_DATA_INCOMPLETE}
        </Text>
      )}
    </Group>
  );
}

/**
 * Таблица счёта Freedom (вкладка «Freedom» на «Позициях», задача 60). Суммы в валюте бумаги — с КОДОМ валюты (не знаком ₽);
 * рубли — отдельной колонкой по курсам bonds. Деньги по валютам — внизу. Мобильная ширина — карточки.
 */
export function FreedomTable({ holdings }: { holdings: BrokerHoldingsResponse }) {
  const isMobile = useMediaQuery('(max-width: 48em)');
  const { positions, cash } = holdings;

  if (isMobile) {
    return (
      <Stack gap="sm" data-testid="freedom-cards">
        {positions.map((p) => (
          <PositionCard key={p.ticker} p={p} />
        ))}
        <Paper withBorder p="sm" radius="md">
          <Text size="xs" c="dimmed" mb={4}>
            Деньги
          </Text>
          <CashBlock cash={cash} />
        </Paper>
        <Paper withBorder p="sm" radius="md">
          <Totals h={holdings} />
        </Paper>
      </Stack>
    );
  }

  return (
    <Stack gap="md">
      <Section
        padding="none"
        title="Счёт Freedom"
        description={`${positions.length} ${positions.length === 1 ? 'позиция' : 'позиций'} · суммы в валюте бумаги`}
      >
        <Table.ScrollContainer minWidth={1000}>
          <Table highlightOnHover data-testid="freedom-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Тикер</Table.Th>
                <Table.Th>Название</Table.Th>
                <Table.Th>Кол-во</Table.Th>
                <Table.Th>Средняя</Table.Th>
                <Table.Th>Текущая</Table.Th>
                <Table.Th>Стоимость</Table.Th>
                <Table.Th>P&amp;L</Table.Th>
                <Table.Th>Стоимость, ₽</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {positions.map((p) => (
                <Table.Tr key={p.ticker} data-testid={`freedom-row-${p.ticker}`}>
                  <Table.Td>
                    <Text fw={500}>{p.ticker}</Text>
                    <Text size="xs" c="dimmed">
                      {FREEDOM_ASSET_CLASS_LABEL[p.assetClass] ?? p.assetClass}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" lineClamp={2}>
                      {p.name}
                    </Text>
                  </Table.Td>
                  <Table.Td>{formatQuantity(p.quantity)}</Table.Td>
                  <Table.Td>{price(p.avgPrice, p.currency)}</Table.Td>
                  <Table.Td>{price(p.lastPrice, p.currency)}</Table.Td>
                  <Table.Td>{formatMoney(p.marketValue, p.currency)}</Table.Td>
                  <Table.Td>
                    <span className={`mvv-num ${signClass(p.unrealizedPnl)}`}>{pnlText(p)}</span>
                    <div className={`bonds-note ${signClass(p.unrealizedPnl)}`}>{formatPercent(p.unrealizedPnlFraction)}</div>
                  </Table.Td>
                  <Table.Td>
                    <RubCell value={p.marketValueRub} />
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Section>
      <Section title="Деньги на счёте">
        <Stack gap="sm">
          <CashBlock cash={cash} />
          <Totals h={holdings} />
        </Stack>
      </Section>
    </Stack>
  );
}
