import { useEffect, useState } from 'react';
import { Badge, Collapse, Loader, SegmentedControl, Table, Tooltip as MantineTooltip, UnstyledButton } from '@mantine/core';
import { EmptyState, ErrorState, Metric, MetricGrid, Page, Section } from '@mvv42/kit';
import { ChartFrame, ChartTooltip, chartColors, pickAxisTicks, useChartTheme } from '@mvv42/kit/charts';
import { IconChevronDown } from '@tabler/icons-react';
import { Bar, BarChart, CartesianGrid, Legend, Tooltip, XAxis, YAxis } from 'recharts';
import { useCashflowStore } from '../store/useCashflowStore';
import { Disclaimer } from '../components/Disclaimer';
import { CHART_EXPLANATIONS } from '../components/charts';
import { formatRub, formatRubCompact, formatMonthLabel, formatDate } from '../utils/format';

const ESTIMATED_HINT = 'Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке';

function EstimatedBadge({ size = 'sm', label = 'оценочно' }: { size?: 'xs' | 'sm'; label?: string }) {
  return (
    <MantineTooltip label={ESTIMATED_HINT} withArrow>
      <Badge size={size} color="yellow">
        {label}
      </Badge>
    </MantineTooltip>
  );
}

const FLOW_TYPE_LABEL: Record<string, string> = {
  Coupon: 'Купон',
  Amortization: 'Амортизация',
  Redemption: 'Погашение',
  Maturity: 'Погашение',
  Offer: 'Оферта',
  Call: 'Колл-опцион',
};

type Horizon = '6m' | '12m' | '24m' | 'all';

function toDateParam(horizon: Horizon): string | undefined {
  if (horizon === 'all') return undefined;
  const months = { '6m': 6, '12m': 12, '24m': 24 }[horizon];
  const d = new Date();
  d.setMonth(d.getMonth() + months);
  return d.toISOString().slice(0, 10);
}

/**
 * Экран денежного потока: календарь поступлений по месяцам (брутто/налог/нетто) +
 * даты освобождения тела долга (этап 09b §B.2).
 */
export function Cashflow() {
  const { byMonth, byPosition, principalReleases, nextPayments, disclaimer, isLoading, error, load } =
    useCashflowStore();
  const [byPositionOpen, setByPositionOpen] = useState(false);
  const [selectedMonth, setSelectedMonth] = useState<string | null>(null);
  const [horizon, setHorizon] = useState<Horizon>('12m');
  const t = useChartTheme();

  useEffect(() => {
    load(toDateParam(horizon));
  }, [load, horizon]);

  const hasEstimatedAny = byMonth.some((m) => m.hasEstimatedFlows);

  const chartData = byMonth.map((m) => ({
    month: formatMonthLabel(m.month),
    'Налог': m.taxRub,
    'Нетто': m.netRub,
    raw: m,
  }));

  const totalNetRub = byMonth.reduce((s, m) => s + m.netRub, 0);
  const totalCouponGross = byMonth.reduce((s, m) => s + m.couponGrossRub, 0);
  const totalPrincipalGross = byMonth.reduce((s, m) => s + m.principalGrossRub, 0);
  const totalTaxRub = byMonth.reduce((s, m) => s + m.taxRub, 0);

  const horizonControl = (
    <SegmentedControl
      value={horizon}
      onChange={(v) => setHorizon(v as Horizon)}
      data={[
        { value: '6m', label: '6 мес' },
        { value: '12m', label: '12 мес' },
        { value: '24m', label: '24 мес' },
        { value: 'all', label: 'Весь срок' },
      ]}
      size="xs"
      aria-label="Горизонт"
    />
  );

  const selectedMonthData = selectedMonth ? byMonth.find((m) => m.month === selectedMonth) : undefined;

  return (
    <Page
      title="Денежный поток"
      description="Купоны, погашения и налог по месяцам"
      width="wide"
      actions={!isLoading && !error ? horizonControl : undefined}
    >
      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить календарь поступлений" error={error} data-testid="cashflow-error" />
        </Section>
      )}

      {!isLoading && !error && (
        <>
          {byMonth.length > 0 && (
            <MetricGrid data-testid="cashflow-kpi-cards">
              <Metric label="Нетто за период" value={formatRub(totalNetRub)} hint="после НДФЛ" />
              <Metric label="Купоны (брутто)" value={formatRub(totalCouponGross)} />
              <Metric label="Возврат тела" value={formatRub(totalPrincipalGross)} hint="погашения, амортизации, оферты" />
              <Metric label="Налог" value={formatRub(totalTaxRub)} hint="оценка НДФЛ" />
            </MetricGrid>
          )}

          {byMonth.length === 0 ? (
            <Section>
              <EmptyState
                title="Нет данных"
                description="Календарь поступлений пуст — появится после синхронизации с брокерским счётом."
                data-testid="cashflow-empty"
              />
            </Section>
          ) : (
            <ChartFrame
              title="Поступления по месяцам"
              description="Нажмите на столбец месяца, чтобы увидеть, какие бумаги формируют поступление."
              data-testid="cashflow-chart"
              explanation={CHART_EXPLANATIONS.cashflowByMonth}
              actions={
                hasEstimatedAny && (
                  <MantineTooltip label={ESTIMATED_HINT} withArrow>
                    <Badge color="yellow" data-testid="estimated-flows-badge">
                      есть оценочные потоки
                    </Badge>
                  </MantineTooltip>
                )
              }
              footer={
                selectedMonthData && selectedMonthData.positions.length > 0 ? (
                  <div className="bonds-drilldown" data-testid="cashflow-month-drill-down">
                    <div className="bonds-drilldown-title">Поступления в {formatMonthLabel(selectedMonth)}</div>
                    <Table>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>Бумага</Table.Th>
                          <Table.Th>Тип</Table.Th>
                          <Table.Th ta="right">Брутто</Table.Th>
                          <Table.Th ta="right">Налог</Table.Th>
                          <Table.Th ta="right">Нетто</Table.Th>
                          <Table.Th>Пометки</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {selectedMonthData.positions.map((pos, idx) => (
                          <Table.Tr key={`${pos.positionId}-${pos.flowType}-${idx}`}>
                            <Table.Td>{pos.name ?? pos.issuer ?? `#${pos.positionId}`}</Table.Td>
                            <Table.Td c="dimmed">{FLOW_TYPE_LABEL[pos.flowType] ?? pos.flowType}</Table.Td>
                            <Table.Td ta="right">{formatRub(pos.grossRub)}</Table.Td>
                            <Table.Td ta="right">{formatRub(pos.taxRub)}</Table.Td>
                            <Table.Td ta="right">{formatRub(pos.netRub)}</Table.Td>
                            <Table.Td>{pos.isEstimated && <EstimatedBadge />}</Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </div>
                ) : undefined
              }
            >
              <BarChart
                data={chartData}
                margin={t.margin}
                style={{ cursor: 'pointer' }}
                onClick={(data) => {
                  const monthKey = ((data as unknown) as { activePayload?: Array<{ payload: { raw?: { month?: string } } }> })
                    ?.activePayload?.[0]?.payload?.raw?.month;
                  if (monthKey) setSelectedMonth((prev) => (prev === monthKey ? null : monthKey));
                }}
              >
                <CartesianGrid {...t.grid} />
                <XAxis dataKey="month" {...t.xAxis} ticks={pickAxisTicks(chartData.map((d) => d.month), t.maxTicks)} />
                <YAxis {...t.yAxis} width={84} tickFormatter={(v: number) => formatRubCompact(v)} />
                <Tooltip
                  {...t.barTooltip}
                  content={({ active, payload, label }) => {
                    if (!active || !payload?.length) return null;
                    const item = payload[0].payload as { raw?: { hasEstimatedFlows?: boolean } };
                    const title = item?.raw?.hasEstimatedFlows ? `${label} (есть оценочные потоки)` : String(label);
                    return (
                      <ChartTooltip
                        title={title}
                        rows={payload.map((entry) => ({
                          label: String(entry.name),
                          value: formatRub(Number(entry.value)),
                          color: entry.color,
                        }))}
                      />
                    );
                  }}
                />
                <Legend {...t.legend} />
                <Bar {...t.bar} dataKey="Нетто" stackId="flow" fill={chartColors.accent} radius={[0, 0, 0, 0]} isAnimationActive={false} />
                <Bar {...t.bar} dataKey="Налог" stackId="flow" fill={chartColors.negative} isAnimationActive={false} />
              </BarChart>
            </ChartFrame>
          )}

          <div className="bonds-grid-2">
            <Section title="Даты освобождения тела долга" padding="none" data-testid="principal-releases">
              {principalReleases.length === 0 ? (
                <EmptyState
                  compact
                  title="Нет событий"
                  description="Нет запланированных дат амортизации/погашения/оферты/колла в горизонте."
                />
              ) : (
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Дата</Table.Th>
                      <Table.Th>Тип</Table.Th>
                      <Table.Th ta="right">Сумма</Table.Th>
                      <Table.Th>Пометки</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {principalReleases.map((r, idx) => (
                      <Table.Tr key={`${r.positionId}-${r.date}-${idx}`}>
                        <Table.Td>{formatDate(r.date)}</Table.Td>
                        <Table.Td>{FLOW_TYPE_LABEL[r.flowType] ?? r.flowType}</Table.Td>
                        <Table.Td ta="right">{formatRub(r.amountRub)}</Table.Td>
                        <Table.Td>{r.isEstimated && <EstimatedBadge />}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Section>

            {nextPayments.length > 0 && (
              <Section title="Ближайшие поступления" padding="none" data-testid="next-payments">
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
                    {nextPayments.map((p, idx) => (
                      <Table.Tr key={idx}>
                        <Table.Td>{formatDate(p.date)}</Table.Td>
                        <Table.Td>{p.name ?? p.issuer ?? '—'}</Table.Td>
                        <Table.Td c="dimmed">{FLOW_TYPE_LABEL[p.flowType] ?? p.flowType}</Table.Td>
                        <Table.Td ta="right">
                          {formatRub(p.netRub)}
                          {p.isEstimated && (
                            <span className="bonds-inline-badge">
                              <EstimatedBadge size="xs" label="~" />
                            </span>
                          )}
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Section>
            )}
          </div>

          {byPosition.length > 0 && (
            <Section
              padding="none"
              title={
                <UnstyledButton
                  className="bonds-disclosure"
                  onClick={() => setByPositionOpen((o) => !o)}
                  aria-expanded={byPositionOpen}
                  data-testid="toggle-by-position"
                >
                  Разбивка по позициям
                  <IconChevronDown size={16} className="bonds-disclosure-icon" data-open={byPositionOpen || undefined} />
                </UnstyledButton>
              }
            >
              <Collapse expanded={byPositionOpen}>
                <Table data-testid="cashflow-by-position-table">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Позиция</Table.Th>
                      <Table.Th ta="right">Брутто</Table.Th>
                      <Table.Th ta="right">Налог</Table.Th>
                      <Table.Th ta="right">Нетто</Table.Th>
                      <Table.Th>Пометки</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {byPosition.map((p) => (
                      <Table.Tr key={p.positionId}>
                        <Table.Td>{p.name ?? p.issuer ?? `#${p.positionId}`}</Table.Td>
                        <Table.Td ta="right">{formatRub(p.grossRub)}</Table.Td>
                        <Table.Td ta="right">{formatRub(p.taxRub)}</Table.Td>
                        <Table.Td ta="right">{formatRub(p.netRub)}</Table.Td>
                        <Table.Td>{p.hasEstimatedFlows && <EstimatedBadge />}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Collapse>
            </Section>
          )}
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Page>
  );
}
