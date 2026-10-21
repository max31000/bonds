import { Badge, Group, Paper, Stack, Table, Text, Tooltip } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useNavigate } from 'react-router-dom';
import { Section } from '@mvv42/kit';
import type { FundRow, FundsTotals } from '../api/types';
import { formatPercent, formatRub, formatRubPrecise } from '../utils/format';
import { FUND_METHOD_LABEL, outlookTooltip } from './fundsCopy';

/** Ячейка «Оценка доходности»: годовая доходность + бейдж метода, тултип с основой (basisLabel), после налога и предупреждениями. */
export function FundOutlookCell({ fund }: { fund: FundRow }) {
  const o = fund.outlook;
  return (
    <Tooltip label={outlookTooltip(o, formatPercent(o.afterTaxYieldFraction))} multiline w={300} withArrow>
      <Group gap={6} wrap="nowrap" data-testid={`fund-outlook-${fund.fundId}`}>
        <Text>{formatPercent(o.expectedAnnualYieldFraction)}</Text>
        {o.expectedAnnualYieldFraction !== null && (
          <Badge size="xs" variant="light" color="gray">
            {FUND_METHOD_LABEL[o.method]}
          </Badge>
        )}
      </Group>
    </Tooltip>
  );
}

function pnlColor(v: number | null): string | undefined {
  return v === null ? undefined : v >= 0 ? 'green' : 'red';
}

function FundCard({ fund, onClick }: { fund: FundRow; onClick: () => void }) {
  const color = pnlColor(fund.unrealizedPnlRub);
  return (
    <Paper withBorder p="sm" radius="md" data-testid={`fund-card-${fund.fundId}`} onClick={onClick} style={{ cursor: 'pointer' }}>
      <Stack gap={4}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Text fw={600} size="sm" lineClamp={2}>
            {fund.name ?? fund.ticker ?? '—'}
          </Text>
          {fund.ticker && (
            <Badge size="sm" color="gray" variant="outline">
              {fund.ticker}
            </Badge>
          )}
        </Group>
        <Text fw={700}>{formatRub(fund.marketValueRub)}</Text>
        <Text size="xs" c="dimmed">
          {fund.quantity} паёв × {formatRubPrecise(fund.currentPriceRub)}
        </Text>
        <Group gap="md">
          <Text size="xs" c={color}>
            P&amp;L {formatRub(fund.unrealizedPnlRub)} ({formatPercent(fund.unrealizedPnlFraction)})
          </Text>
        </Group>
        <Group gap="md">
          <Text size="xs" c="dimmed">
            Выплаты (нетто): <Text span fw={600}>{formatRub(fund.distributionsNetRub)}</Text>
          </Text>
          <Text size="xs" c="dimmed">
            12м (факт): <Text span fw={600}>{formatPercent(fund.performance.m12?.returnFraction)}</Text>
          </Text>
        </Group>
        <Group gap="md" align="center">
          <Text size="xs" c="dimmed">
            Оценка:
          </Text>
          <FundOutlookCell fund={fund} />
        </Group>
        <Group gap="md">
          <Text size="xs" c="dimmed">
            Доход в мес. (оценка): <Text span fw={600}>{formatRub(fund.outlook.expectedMonthlyIncomeRub)}</Text>
          </Text>
          <Text size="xs" c="dimmed">
            Доля: <Text span fw={600}>{formatPercent(fund.shareOfPortfolioFraction)}</Text>
          </Text>
        </Group>
      </Stack>
    </Paper>
  );
}

/** Таблица фондов (вкладка «Фонды» на «Позициях»): десктоп — таблица с итогами, мобильная версия — карточки. */
export function FundsTable({ funds, totals }: { funds: FundRow[]; totals: FundsTotals | null }) {
  const navigate = useNavigate();
  const isMobile = useMediaQuery('(max-width: 48em)');
  const monthlyTotal = funds.reduce((s, f) => s + (f.outlook.expectedMonthlyIncomeRub ?? 0), 0);
  const hasMonthly = funds.some((f) => f.outlook.expectedMonthlyIncomeRub !== null);
  const totalPnlColor = pnlColor(totals?.unrealizedPnlRub ?? null);

  if (isMobile) {
    return (
      <Stack gap="sm" data-testid="funds-cards">
        <Paper withBorder p="sm" radius="md" data-testid="funds-totals-mobile">
          <Text size="xs" c="dimmed">
            Итого по фондам
          </Text>
          <Text fw={700}>{formatRub(totals?.marketValueRub)}</Text>
          <Text size="xs" c={totalPnlColor}>
            P&amp;L {formatRub(totals?.unrealizedPnlRub)} · выплаты (нетто) {formatRub(totals?.incomeNetRub)}
          </Text>
        </Paper>
        {funds.map((f) => (
          <FundCard key={f.fundId} fund={f} onClick={() => navigate(`/funds/${f.fundId}`)} />
        ))}
      </Stack>
    );
  }

  return (
    <Section
      padding="none"
      title="Паи фондов"
      description={`${funds.length} ${funds.length === 1 ? 'фонд' : funds.length < 5 ? 'фонда' : 'фондов'} · строка открывает карточку`}
    >
      <Table.ScrollContainer minWidth={1100}>
        <Table highlightOnHover data-testid="funds-table">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Фонд</Table.Th>
              <Table.Th>Кол-во</Table.Th>
              <Table.Th>Цена пая</Table.Th>
              <Table.Th>Стоимость</Table.Th>
              <Table.Th>Ср. цена</Table.Th>
              <Table.Th>P&amp;L</Table.Th>
              <Table.Th>Выплаты получено (нетто)</Table.Th>
              <Table.Th>Доходность 12м (факт)</Table.Th>
              <Table.Th>Оценка доходности</Table.Th>
              <Table.Th>Доход в мес. (оценка)</Table.Th>
              <Table.Th>Доля портфеля</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {funds.map((f) => {
              const color = pnlColor(f.unrealizedPnlRub);
              return (
                <Table.Tr
                  key={f.fundId}
                  data-testid={`fund-row-${f.fundId}`}
                  onClick={() => navigate(`/funds/${f.fundId}`)}
                  style={{ cursor: 'pointer' }}
                >
                  <Table.Td>
                    <Text fw={500}>{f.name ?? f.ticker ?? '—'}</Text>
                    {f.ticker && (
                      <Text size="xs" c="dimmed">
                        {f.ticker}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>{f.quantity}</Table.Td>
                  <Table.Td>{formatRubPrecise(f.currentPriceRub)}</Table.Td>
                  <Table.Td>{formatRub(f.marketValueRub)}</Table.Td>
                  <Table.Td>{formatRubPrecise(f.avgCostRub)}</Table.Td>
                  <Table.Td>
                    <Text c={color} fw={500}>
                      {formatRub(f.unrealizedPnlRub)}
                    </Text>
                    <Text c={color} size="xs">
                      {formatPercent(f.unrealizedPnlFraction)}
                    </Text>
                  </Table.Td>
                  <Table.Td>{formatRub(f.distributionsNetRub)}</Table.Td>
                  <Table.Td>{formatPercent(f.performance.m12?.returnFraction)}</Table.Td>
                  <Table.Td>
                    <FundOutlookCell fund={f} />
                  </Table.Td>
                  <Table.Td>{formatRub(f.outlook.expectedMonthlyIncomeRub)}</Table.Td>
                  <Table.Td>{formatPercent(f.shareOfPortfolioFraction)}</Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
          <Table.Tfoot>
            <Table.Tr data-testid="funds-totals-row">
              <Table.Th>Итого</Table.Th>
              <Table.Th />
              <Table.Th />
              <Table.Th>{formatRub(totals?.marketValueRub)}</Table.Th>
              <Table.Th />
              <Table.Th>
                <Text c={totalPnlColor} fw={700} size="sm">
                  {formatRub(totals?.unrealizedPnlRub)}
                </Text>
              </Table.Th>
              <Table.Th>{formatRub(totals?.incomeNetRub)}</Table.Th>
              <Table.Th />
              <Table.Th />
              <Table.Th>{hasMonthly ? formatRub(monthlyTotal) : '—'}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Tfoot>
        </Table>
      </Table.ScrollContainer>
    </Section>
  );
}
