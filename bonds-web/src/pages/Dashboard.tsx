import { useEffect, useMemo } from 'react';
import { Title, Stack, SimpleGrid, Paper, Text, Group, Loader, Center, UnstyledButton, Table, Badge } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip } from 'recharts';
import { fetchPositions } from '../api/positions';
import { fetchXirr, fetchComposition } from '../api/analytics';
import { fetchCashflow } from '../api/cashflow';
import { useLiveStore } from '../store/useLiveStore';
import { useSignalsStore } from '../store/useSignalsStore';
import { useLiveQuotes } from '../hooks/useLiveQuotes';
import { useWidgetData } from '../hooks/useWidgetData';
import { Disclaimer } from '../components/Disclaimer';
import { PortfolioIntradayChart } from '../components/PortfolioIntradayChart';
import { ChartCard, ChartTooltip, CHART_COLORS, CHART_HEIGHT_COMPACT, CHART_EXPLANATIONS } from '../components/charts';
import type { CompositionSlice, XirrResponse, CashflowResponse, PositionsResponse } from '../api/types';
import { formatRub, formatPercent, formatSharePercent, formatDate } from '../utils/format';

const FLOW_TYPE_LABEL: Record<string, string> = {
  Coupon: 'Купон',
  Amortization: 'Амортизация',
  Redemption: 'Погашение',
  Maturity: 'Погашение',
  Offer: 'Оферта',
  Call: 'Колл-опцион',
};

/**
 * KPI-карточка «Стоимость портфеля» — сумма из GET /api/positions (последний синк) с дельтой за
 * день из GET /api/live/positions, если live-контур (plan/16) уже накопил хотя бы один тик.
 * Дельта скрывается, пока живых данных нет — план прямо требует не выдумывать значение.
 */
function PortfolioValueKpi({ positions }: { positions: PositionsResponse | null }) {
  const liveTotal = useLiveStore((s) => s.totalMarketValueRub);

  const staticTotal = useMemo(
    () => (positions ? positions.positions.reduce((sum, p) => sum + p.marketValueRub, 0) : null),
    [positions],
  );

  const displayValue = liveTotal ?? staticTotal;
  const deltaRub = liveTotal !== null && staticTotal !== null ? liveTotal - staticTotal : null;
  const deltaPercent = deltaRub !== null && staticTotal ? deltaRub / staticTotal : null;

  return (
    <Paper withBorder p="md" radius="md" data-testid="kpi-portfolio-value">
      <Text size="xs" c="dimmed">
        Стоимость портфеля
      </Text>
      <Text fw={700} size="xl">
        {displayValue === null ? '—' : formatRub(displayValue)}
      </Text>
      {deltaPercent !== null && (
        <Text size="xs" c={deltaPercent >= 0 ? 'green' : 'red'} data-testid="kpi-portfolio-value-delta">
          {deltaPercent >= 0 ? '+' : ''}
          {formatPercent(deltaPercent)} за день
        </Text>
      )}
    </Paper>
  );
}

function XirrKpi({ xirr, error }: { xirr: XirrResponse | null; error: string | null }) {
  return (
    <Paper withBorder p="md" radius="md" data-testid="kpi-xirr">
      <Text size="xs" c="dimmed">
        Доходность (XIRR)
      </Text>
      {error ? (
        <Text size="sm" c="dimmed" data-testid="kpi-xirr-error">
          нет данных
        </Text>
      ) : (
        <Text fw={700} size="xl">
          {formatPercent(xirr?.currentXirr ?? null)}
        </Text>
      )}
    </Paper>
  );
}

function NextPaymentKpi({ cashflow, error }: { cashflow: CashflowResponse | null; error: string | null }) {
  const next = cashflow?.nextPayments?.[0];

  return (
    <Paper withBorder p="md" radius="md" data-testid="kpi-next-payment">
      <Text size="xs" c="dimmed">
        Ближайшее поступление
      </Text>
      {error ? (
        <Text size="sm" c="dimmed" data-testid="kpi-next-payment-error">
          нет данных
        </Text>
      ) : !next ? (
        <Text size="sm" c="dimmed">
          нет запланированных поступлений
        </Text>
      ) : (
        <>
          <Text fw={700} size="xl">
            {formatRub(next.netRub)}
          </Text>
          <Text size="xs" c="dimmed">
            {formatDate(next.date)} — {FLOW_TYPE_LABEL[next.flowType] ?? next.flowType}
            {next.name ? ` · ${next.name}` : next.issuer ? ` · ${next.issuer}` : ''}
          </Text>
        </>
      )}
    </Paper>
  );
}

function UnreadSignalsKpi() {
  const navigate = useNavigate();
  const signals = useSignalsStore((s) => s.signals);
  const loadSignals = useSignalsStore((s) => s.load);

  useEffect(() => {
    loadSignals();
  }, [loadSignals]);

  const unreadCount = signals.filter((s) => !s.isRead).length;

  return (
    <UnstyledButton onClick={() => navigate('/signals')} data-testid="kpi-unread-signals">
      <Paper withBorder p="md" radius="md" style={{ cursor: 'pointer' }}>
        <Text size="xs" c="dimmed">
          Непрочитанные сигналы
        </Text>
        <Group gap={8} align="baseline">
          <Text fw={700} size="xl">
            {unreadCount}
          </Text>
          {unreadCount > 0 && (
            <Badge size="sm" color="violet" variant="light">
              смотреть
            </Badge>
          )}
        </Group>
      </Paper>
    </UnstyledButton>
  );
}

/** График стоимости — интрадей-виджет из plan/16, а не отдельная реализация (переиспользуем как есть). */
function ValueChartWidget() {
  return <PortfolioIntradayChart />;
}

/** Мини-композиция по эмитентам: топ-5 + «прочие» (plan/18 часть B.3), компактный donut. */
function MiniCompositionWidget() {
  const { data, isLoading, error } = useWidgetData(fetchComposition, []);

  const slices = useMemo((): CompositionSlice[] => {
    if (!data) return [];
    const sorted = [...data.byIssuer].sort((a, b) => b.marketValueRub - a.marketValueRub);
    const top = sorted.slice(0, 5);
    const rest = sorted.slice(5);
    if (rest.length === 0) return top;
    const restSum = rest.reduce((s, r) => s + r.marketValueRub, 0);
    const restShare = rest.reduce((s, r) => s + r.sharePercent, 0);
    return [...top, { key: 'Прочие', marketValueRub: restSum, sharePercent: restShare }];
  }, [data]);

  return (
    <ChartCard title="Композиция по эмитентам" data-testid="widget-composition" explanation={CHART_EXPLANATIONS.composition}>
      {isLoading && (
        <Center py="lg">
          <Loader size="sm" />
        </Center>
      )}
      {!isLoading && error && (
        <Text size="sm" c="dimmed" data-testid="widget-composition-error">
          Нет данных — запустите синк.
        </Text>
      )}
      {!isLoading && !error && slices.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="widget-composition-empty">
          Нет данных — запустите синк.
        </Text>
      )}
      {!isLoading && !error && slices.length > 0 && (
        <ResponsiveContainer width="100%" height={CHART_HEIGHT_COMPACT}>
          <PieChart>
            <Pie data={slices} dataKey="sharePercent" nameKey="key" cx="50%" cy="50%" outerRadius={80} innerRadius={40}>
              {slices.map((s, idx) => (
                <Cell key={s.key} fill={CHART_COLORS[idx % CHART_COLORS.length]} />
              ))}
            </Pie>
            <Tooltip
              content={({ payload }) => {
                const slice = payload?.[0]?.payload as CompositionSlice | undefined;
                if (!slice) return null;
                return (
                  <ChartTooltip
                    title={slice.key}
                    rows={[
                      { label: 'Доля', value: formatSharePercent(slice.sharePercent) },
                      { label: 'Стоимость', value: formatRub(slice.marketValueRub) },
                    ]}
                  />
                );
              }}
            />
            <Legend verticalAlign="bottom" height={48} />
          </PieChart>
        </ResponsiveContainer>
      )}
    </ChartCard>
  );
}

/** Ближайшие 30 дней денежного потока — компактный список из GET /api/cashflow (plan/18 часть B.4). */
function UpcomingCashflowWidget() {
  const { data, isLoading, error } = useWidgetData(fetchCashflow, []);

  const upcoming = useMemo(() => {
    if (!data) return [];
    const cutoff = new Date();
    cutoff.setDate(cutoff.getDate() + 30);
    return data.nextPayments.filter((p) => new Date(p.date) <= cutoff).slice(0, 8);
  }, [data]);

  return (
    <Paper withBorder p="md" radius="md" data-testid="widget-upcoming-cashflow">
      <Text fw={600} mb="xs">
        Ближайшие 30 дней
      </Text>
      {isLoading && (
        <Center py="lg">
          <Loader size="sm" />
        </Center>
      )}
      {!isLoading && error && (
        <Text size="sm" c="dimmed" data-testid="widget-upcoming-cashflow-error">
          Нет данных — запустите синк.
        </Text>
      )}
      {!isLoading && !error && upcoming.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="widget-upcoming-cashflow-empty">
          Нет запланированных поступлений в ближайшие 30 дней.
        </Text>
      )}
      {!isLoading && !error && upcoming.length > 0 && (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Дата</Table.Th>
              <Table.Th>Бумага</Table.Th>
              <Table.Th>Тип</Table.Th>
              <Table.Th>Нетто</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {upcoming.map((p, idx) => (
              <Table.Tr key={idx}>
                <Table.Td>{formatDate(p.date)}</Table.Td>
                <Table.Td>{p.name ?? p.issuer ?? '—'}</Table.Td>
                <Table.Td>{FLOW_TYPE_LABEL[p.flowType] ?? p.flowType}</Table.Td>
                <Table.Td>{formatRub(p.netRub)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Paper>
  );
}

/**
 * Главная страница-дашборд (plan/18 часть B) — «взгляд за 5 секунд»: сколько всего, что
 * изменилось, что требует внимания. Таблица позиций переехала на /positions.
 * <p>
 * Каждая карточка грузит свои данные независимо (useWidgetData/собственные сторы) — отказ
 * одного эндпоинта не роняет остальные виджеты дашборда.
 */
export function Dashboard() {
  useLiveQuotes();

  const { data: positions } = useWidgetData(fetchPositions, []);
  const { data: xirr, error: xirrError } = useWidgetData(fetchXirr, []);
  const { data: cashflow, error: cashflowError } = useWidgetData(fetchCashflow, []);

  return (
    <Stack gap="md">
      <Title order={2}>Обзор</Title>

      <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }} data-testid="dashboard-kpi-row">
        <PortfolioValueKpi positions={positions} />
        <XirrKpi xirr={xirr} error={xirrError} />
        <NextPaymentKpi cashflow={cashflow} error={cashflowError} />
        <UnreadSignalsKpi />
      </SimpleGrid>

      <ValueChartWidget />

      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <MiniCompositionWidget />
        <UpcomingCashflowWidget />
      </SimpleGrid>

      <Disclaimer />
    </Stack>
  );
}
