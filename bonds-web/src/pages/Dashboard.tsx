import { useEffect, useMemo } from 'react';
import { Badge, Group, Loader, Table, UnstyledButton } from '@mantine/core';
import { EmptyState, ErrorState, HelpHint, Metric, MetricGrid, Page, Section } from '@mvv42/kit';
import { ChartFrame, ChartTooltip, CHART_HEIGHT_COMPACT, seriesColor, useChartTheme } from '@mvv42/kit/charts';
import { useNavigate } from 'react-router-dom';
import { Cell, Legend, Pie, PieChart, Tooltip } from 'recharts';
import { fetchPositions } from '../api/positions';
import { fetchXirr, fetchComposition } from '../api/analytics';
import { fetchCashflow } from '../api/cashflow';
import { fetchLivePositions } from '../api/live';
import { fetchFunds, fetchPortfolioSummary } from '../api/funds';
import { useLiveStore } from '../store/useLiveStore';
import { countAttention, useSignalsStore } from '../store/useSignalsStore';
import { useLiveQuotes } from '../hooks/useLiveQuotes';
import { useWidgetData } from '../hooks/useWidgetData';
import { Disclaimer } from '../components/Disclaimer';
import { PortfolioIntradayChart } from '../components/PortfolioIntradayChart';
import { CHART_EXPLANATIONS } from '../components/charts';
import { DIRTY_PRICE_TOOLTIP_TEXT } from '../utils/positionsDisplay';
import type {
  CompositionSlice,
  XirrResponse,
  CashflowResponse,
  LivePositionsResponse,
  PositionsResponse,
  PortfolioSummary,
  FundsResponse,
} from '../api/types';
import { formatRub, formatPercent, formatSharePercent, formatDate } from '../utils/format';

/** Та же подсказка про «грязные» цены, что и у таблицы позиций (positionsDisplay.ts) — один источник текста. */
const DIRTY_PRICE_EXPLANATION = DIRTY_PRICE_TOOLTIP_TEXT;

const FLOW_TYPE_LABEL: Record<string, string> = {
  Coupon: 'Купон',
  Amortization: 'Амортизация',
  Redemption: 'Погашение',
  Maturity: 'Погашение',
  Offer: 'Оферта',
  Call: 'Колл-опцион',
};

/**
 * KPI «Стоимость портфеля» — сумма из GET /api/positions (последний синк) с дельтой за день из
 * GET /api/live/positions, если live-контур уже накопил хотя бы один тик. Дельта
 * скрывается, пока живых данных нет — план прямо требует не выдумывать значение.
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
    <Metric
      data-testid="kpi-portfolio-value"
      label={
        <Group gap={4} wrap="nowrap" component="span">
          Стоимость облигаций
          <HelpHint label="Пояснение: Стоимость облигаций" data-testid="kpi-portfolio-value-explain-icon">{DIRTY_PRICE_EXPLANATION}</HelpHint>
        </Group>
      }
      value={displayValue === null ? '—' : formatRub(displayValue)}
      delta={
        deltaPercent !== null
          ? {
              value: (
                <span data-testid="kpi-portfolio-value-delta">
                  {deltaPercent >= 0 ? '+' : ''}
                  {formatPercent(deltaPercent)}
                </span>
              ),
              tone: deltaPercent > 0 ? 'positive' : deltaPercent < 0 ? 'negative' : 'neutral',
            }
          : undefined
      }
      hint={deltaPercent !== null ? 'за день' : 'по последнему синку'}
    />
  );
}

/**
 * Свободные деньги на счёте (T-Invest GetPortfolio, последний синк) и итог «облигации + деньги» —
 * сюда приходят погашения и купоны; без них итог сервиса не сходился с приложением брокера.
 */
function CashKpi({ live, bondsTotal }: { live: LivePositionsResponse | null; bondsTotal: number | null }) {
  const cash = live?.cashRub ?? null;
  const accountTotal = cash !== null && bondsTotal !== null ? bondsTotal + cash : null;
  return (
    <Metric
      data-testid="kpi-cash"
      label="Деньги на счёте"
      value={cash === null ? '—' : formatRub(cash)}
      hint={accountTotal !== null ? `итого со счётом: ${formatRub(accountTotal)}` : 'появится после синка'}
    />
  );
}

function XirrKpi({ xirr, error, summary }: { xirr: XirrResponse | null; error: string | null; summary: PortfolioSummary | null }) {
  // «XIRR портфеля» (облигации + фонды) — из /api/portfolio/summary, та же методика, что на странице «Доходы».
  const portfolioXirr = summary?.portfolioXirrFraction ?? null;
  return (
    <Metric
      data-testid="kpi-xirr"
      label="XIRR облигаций"
      value={error ? <span data-testid="kpi-xirr-error">—</span> : formatPercent(xirr?.currentXirr ?? null)}
      hint={
        error
          ? 'нет данных'
          : portfolioXirr !== null
            ? `годовых · XIRR портфеля (с фондами): ${formatPercent(portfolioXirr)}`
            : 'годовых, по операциям счёта'
      }
    />
  );
}

/**
 * Общий баланс = облигации + фонды + деньги (GET /api/portfolio/summary). Подсказка с разбивкой; если итог расходится с
 * брокерским больше чем на 1% — пояснение (другие активы/валюта на счёте). Отказ эндпоинта даёт «—», остальные KPI не затронуты.
 */
function TotalBalanceKpi({ summary, error }: { summary: PortfolioSummary | null; error: string | null }) {
  if (error || !summary) {
    return (
      <Metric
        data-testid="kpi-total-balance"
        label="Стоимость портфеля"
        value={<span data-testid="kpi-total-balance-error">—</span>}
        hint={error ? 'нет данных' : 'загрузка…'}
      />
    );
  }
  const diff = summary.brokerDiffRub;
  const mismatch = diff !== null && summary.totalRub > 0 && Math.abs(diff) / summary.totalRub > 0.01;
  const breakdown = `облигации ${formatRub(summary.bondsValueRub)} · фонды ${formatRub(summary.fundsValueRub)} · деньги ${formatRub(summary.cashRub)}`;
  return (
    <Metric
      data-testid="kpi-total-balance"
      label={
        <Group gap={4} wrap="nowrap" component="span">
          Стоимость портфеля
          <HelpHint label="Пояснение: Стоимость портфеля" data-testid="kpi-total-balance-explain-icon">
            Облигации + фонды + свободные деньги на счёте по последнему синку. Облигации — по грязным ценам (с НКД). {breakdown}.
          </HelpHint>
        </Group>
      }
      value={formatRub(summary.totalRub)}
      hint={
        mismatch ? (
          <span data-testid="kpi-total-balance-mismatch">
            расходится с брокером на {formatRub(Math.abs(diff))} (другие активы/валюта)
          </span>
        ) : (
          breakdown
        )
      }
    />
  );
}

/** KPI «Фонды»: стоимость паёв и нереализованный P&L — только если фонды есть. */
function FundsKpi({ funds }: { funds: FundsResponse | null }) {
  if (!funds || funds.funds.length === 0) return null;
  const { marketValueRub, unrealizedPnlRub } = funds.totals;
  return (
    <Metric
      data-testid="kpi-funds"
      label="Фонды"
      value={formatRub(marketValueRub)}
      delta={{
        value: <span data-testid="kpi-funds-pnl">P&amp;L {formatRub(unrealizedPnlRub)}</span>,
        tone: unrealizedPnlRub > 0 ? 'positive' : unrealizedPnlRub < 0 ? 'negative' : 'neutral',
      }}
      hint="паи БПИФ, по цене пая"
    />
  );
}

function NextPaymentKpi({ cashflow, error }: { cashflow: CashflowResponse | null; error: string | null }) {
  const next = cashflow?.nextPayments?.[0];

  if (error) {
    return (
      <Metric
        data-testid="kpi-next-payment"
        label="Ближайшее поступление"
        value={<span data-testid="kpi-next-payment-error">—</span>}
        hint="нет данных"
      />
    );
  }
  return (
    <Metric
      data-testid="kpi-next-payment"
      label="Ближайшее поступление"
      value={next ? formatRub(next.netRub) : '—'}
      hint={
        next
          ? `${formatDate(next.date)} · ${FLOW_TYPE_LABEL[next.flowType] ?? next.flowType}${
              next.name ? ` · ${next.name}` : next.issuer ? ` · ${next.issuer}` : ''
            }`
          : 'нет запланированных поступлений'
      }
    />
  );
}

function UnreadSignalsKpi() {
  const navigate = useNavigate();
  const signals = useSignalsStore((s) => s.signals);
  const loadSignals = useSignalsStore((s) => s.load);

  useEffect(() => {
    loadSignals();
  }, [loadSignals]);

  // Только активные непрочитанные Critical+Warning — Info и подтверждённые не шумят.
  const unreadCount = countAttention(signals);

  return (
    <UnstyledButton
      className="bonds-metric-link"
      onClick={() => navigate('/signals')}
      data-testid="kpi-unread-signals"
    >
      <Metric
        label="События, требующие внимания"
        value={
          <Group gap={8} align="center" component="span">
            {unreadCount}
            {unreadCount > 0 && (
              <Badge size="sm" variant="light">
                смотреть
              </Badge>
            )}
          </Group>
        }
        hint="оферты, концентрация лимитов"
      />
    </UnstyledButton>
  );
}

/** Мини-композиция по эмитентам: топ-5 + «прочие», компактный donut. */
function MiniCompositionWidget() {
  const { data, isLoading, error } = useWidgetData(fetchComposition, []);
  const t = useChartTheme();

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
    <ChartFrame
      title="Композиция по эмитентам"
      data-testid="widget-composition"
      explanation={CHART_EXPLANATIONS.composition}
      height={CHART_HEIGHT_COMPACT + 40}
      loading={isLoading}
      error={error ? new Error(error) : undefined}
      empty={!isLoading && !error && slices.length === 0}
      emptyTitle="Нет данных"
      emptyDescription="Запустите синк — состав портфеля появится после него."
    >
      <PieChart>
        <Pie
          data={slices}
          dataKey="sharePercent"
          nameKey="key"
          cx={t.isCompact ? '50%' : '32%'}
          cy={t.isCompact ? '42%' : '50%'}
          outerRadius={t.isCompact ? '70%' : '80%'}
          innerRadius={t.isCompact ? '46%' : '54%'}
          paddingAngle={1}
          stroke="var(--mvv-surface)"
          strokeWidth={2}
          isAnimationActive={false}
        >
          {slices.map((s, idx) => (
            <Cell key={s.key} fill={s.key === 'Прочие' ? 'var(--mvv-text-3)' : seriesColor(idx)} />
          ))}
        </Pie>
        <Tooltip
          {...t.barTooltip}
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
        <Legend
          {...t.legend}
          layout={t.isCompact ? 'horizontal' : 'vertical'}
          verticalAlign={t.isCompact ? 'bottom' : 'middle'}
          align={t.isCompact ? 'center' : 'right'}
          height={undefined}
        />
      </PieChart>
    </ChartFrame>
  );
}

/** Ближайшие 30 дней денежного потока — компактный список из GET /api/cashflow. */
function UpcomingCashflowWidget() {
  const { data, isLoading, error } = useWidgetData(fetchCashflow, []);

  const upcoming = useMemo(() => {
    if (!data) return [];
    const cutoff = new Date();
    cutoff.setDate(cutoff.getDate() + 30);
    return data.nextPayments.filter((p) => new Date(p.date) <= cutoff).slice(0, 8);
  }, [data]);

  return (
    <Section title="Ближайшие 30 дней" padding="none" data-testid="widget-upcoming-cashflow">
      {isLoading && (
        <div className="bonds-center">
          <Loader size="sm" />
        </div>
      )}
      {!isLoading && error && <ErrorState compact error={error} data-testid="widget-upcoming-cashflow-error" />}
      {!isLoading && !error && upcoming.length === 0 && (
        <EmptyState
          compact
          title="Поступлений нет"
          description="В ближайшие 30 дней купонов и погашений не запланировано."
          data-testid="widget-upcoming-cashflow-empty"
        />
      )}
      {!isLoading && !error && upcoming.length > 0 && (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Дата</Table.Th>
              <Table.Th>Бумага</Table.Th>
              <Table.Th>Тип</Table.Th>
              <Table.Th ta="right">Нетто</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {upcoming.map((p, idx) => (
              <Table.Tr key={idx}>
                <Table.Td>{formatDate(p.date)}</Table.Td>
                <Table.Td>{p.name ?? p.issuer ?? '—'}</Table.Td>
                <Table.Td c="dimmed">{FLOW_TYPE_LABEL[p.flowType] ?? p.flowType}</Table.Td>
                <Table.Td ta="right">{formatRub(p.netRub)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Section>
  );
}

/**
 * Главная страница-дашборд — «взгляд за 5 секунд»: сколько всего, что
 * изменилось, что требует внимания. Таблица позиций переехала на /positions.
 * <p>
 * Каждая карточка грузит свои данные независимо (useWidgetData/собственные сторы) — отказ
 * одного эндпоинта не роняет остальные виджеты дашборда.
 */
export function Dashboard() {
  useLiveQuotes();

  const { data: positions } = useWidgetData(fetchPositions, []);
  const { data: live } = useWidgetData(fetchLivePositions, []);
  const liveTotal = useLiveStore((s) => s.totalMarketValueRub);
  const bondsTotal = useMemo(
    () => liveTotal ?? live?.totalMarketValueRub ?? (positions ? positions.positions.reduce((sum, p) => sum + p.marketValueRub, 0) : null),
    [liveTotal, live, positions],
  );
  const { data: xirr, error: xirrError } = useWidgetData(fetchXirr, []);
  // Фонды (задача 53): независимые виджеты — отказ любого не роняет остальные KPI.
  const { data: summary, error: summaryError } = useWidgetData(fetchPortfolioSummary, []);
  const { data: funds } = useWidgetData(fetchFunds, []);
  const { data: cashflow, error: cashflowError } = useWidgetData(fetchCashflow, []);

  return (
    <Page title="Обзор" description="Стоимость, доходность и ближайшие поступления портфеля" width="wide">
      <MetricGrid data-testid="dashboard-kpi-row">
        <TotalBalanceKpi summary={summary} error={summaryError} />
        <PortfolioValueKpi positions={positions} />
        <FundsKpi funds={funds} />
        <CashKpi live={live} bondsTotal={bondsTotal} />
        <XirrKpi xirr={xirr} error={xirrError} summary={summary} />
        <NextPaymentKpi cashflow={cashflow} error={cashflowError} />
        <UnreadSignalsKpi />
      </MetricGrid>

      <div data-testid="dashboard-value-chart">
        <PortfolioIntradayChart explanation={CHART_EXPLANATIONS.dashboardValue} />
      </div>

      <div className="bonds-grid-2">
        <MiniCompositionWidget />
        <UpcomingCashflowWidget />
      </div>

      <Disclaimer />
    </Page>
  );
}
