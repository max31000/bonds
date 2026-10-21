import { useEffect, useMemo } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Group, Loader, Select, Table, Text } from '@mantine/core';
import { EmptyState, ErrorState, HelpHint, Metric, MetricGrid, Page, Section } from '@mvv42/kit';
import { ChartFrame, ChartTooltip, chartColors, pickAxisTicks, useChartTheme } from '@mvv42/kit/charts';
import { IconArrowLeft } from '@tabler/icons-react';
import { Bar, BarChart, CartesianGrid, Line, LineChart, ReferenceDot, ReferenceLine, Tooltip, XAxis, YAxis } from 'recharts';
import { useFundsStore } from '../store/useFundsStore';
import { Disclaimer } from '../components/Disclaimer';
import { CHART_EXPLANATIONS } from '../components/charts';
import {
  FUND_AFTER_TAX_NOTE,
  FUND_CONFIDENCE_LABEL,
  FUND_DATA_INCOMPLETE,
  FUND_KEY_RATE_NOTE,
  FUND_METHOD_LABEL,
  FUND_METHOD_OPTIONS,
  FUND_OPERATION_LABEL,
  FUND_WARNING_LABEL,
} from '../components/fundsCopy';
import type { FundForecastMethod } from '../api/types';
import { formatDate, formatMonthLabel, formatPercent, formatRub, formatRubCompact, formatRubPrecise } from '../utils/format';

function MetricCard({ label, value, explanation, hint }: { label: string; value: string; explanation: string; hint?: string }) {
  return (
    <Metric
      data-testid={`fund-metric-${label}`}
      label={
        <Group gap={4} wrap="nowrap" component="span">
          {label}
          <HelpHint label={`Пояснение: ${label}`}>{explanation}</HelpHint>
        </Group>
      }
      value={value}
      hint={hint}
    />
  );
}

/**
 * Карточка фонда (БПИФ), задача 53: метрики (P&L — методика «Доходов»), график цены пая с линией средней цены входа и датами
 * выплат, выплаты по месяцам, блок «Прогноз (оценка)» с выбором метода, журнал операций пользователя. Работает и для
 * проданного фонда (qty 0).
 */
export function FundDetail() {
  const { fundId } = useParams<{ fundId: string }>();
  const navigate = useNavigate();
  const { detail, detailLoading, detailError, settingsSaving, settingsError, loadDetail, saveSettings } = useFundsStore();
  const t = useChartTheme();

  useEffect(() => {
    if (fundId) loadDetail(fundId);
  }, [fundId, loadDetail]);

  const priceData = useMemo(
    () => (detail ? detail.priceHistory.map((p) => ({ date: p.date, label: formatDate(p.date), priceRub: p.closeRub })) : []),
    [detail],
  );

  // Маркеры выплат: ближайшая по дате точка цены ≤ даты фиксации (иначе ReferenceDot без X-совпадения не отрисуется).
  const distributionMarkers = useMemo(() => {
    if (!detail) return [];
    return detail.distributions
      .map((d) => {
        let match: (typeof priceData)[number] | undefined;
        for (const p of priceData) if (p.date <= d.recordDate) match = p;
        return match ? { key: d.recordDate, label: match.label, priceRub: match.priceRub } : null;
      })
      .filter((m): m is NonNullable<typeof m> => m !== null);
  }, [detail, priceData]);

  const monthlyDistributions = useMemo(() => {
    if (!detail) return [];
    const byMonth = new Map<string, { amount: number; yieldFraction: number }>();
    for (const d of detail.distributions) {
      const key = d.recordDate.slice(0, 7);
      const cur = byMonth.get(key) ?? { amount: 0, yieldFraction: 0 };
      cur.amount += d.amountPerShareRub;
      cur.yieldFraction += d.yieldOnPriceFraction ?? 0;
      byMonth.set(key, cur);
    }
    return [...byMonth.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([month, v]) => ({ month, label: formatMonthLabel(month), amount: v.amount, yieldFraction: v.yieldFraction }));
  }, [detail]);

  const backButton = (
    <Button
      variant="subtle"
      color="gray"
      size="xs"
      leftSection={<IconArrowLeft size={14} />}
      onClick={() => navigate('/positions?tab=funds')}
      data-testid="back-to-funds"
    >
      Фонды
    </Button>
  );

  const fund = detail?.fund;
  const outlook = fund?.outlook;

  return (
    <Page
      width="wide"
      data-testid="fund-detail-page"
      eyebrow={backButton}
      title={fund ? (fund.name ?? fund.ticker ?? 'Фонд') : 'Фонд'}
      description={fund ? [fund.ticker, fund.managementCompany].filter(Boolean).join(' · ') || undefined : undefined}
    >
      {detailLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!detailLoading && detailError && (
        <Section>
          <ErrorState title="Не удалось загрузить фонд" error={detailError} data-testid="fund-detail-error" />
        </Section>
      )}

      {!detailLoading && !detailError && detail && fund && outlook && (
        <>
          {fund.dataIncomplete && (
            <Text size="sm" c="orange" role="alert" data-testid="fund-detail-incomplete">
              {FUND_DATA_INCOMPLETE}
            </Text>
          )}
          <MetricGrid cols={3} data-testid="fund-detail-metrics">
            <MetricCard
              label="Стоимость"
              value={formatRub(fund.marketValueRub)}
              hint={fund.quantity > 0 ? `${fund.quantity} паёв × ${formatRubPrecise(fund.currentPriceRub)}` : 'фонд продан'}
              explanation="Количество паёв × цена пая по оценке брокера (рубли за пай)."
            />
            <MetricCard
              label="P&L"
              value={formatRub(fund.unrealizedPnlRub)}
              hint={`${formatPercent(fund.unrealizedPnlFraction)} к себестоимости · итог с выплатами ${formatRub(fund.totalResultRub)}`}
              explanation="Нереализованный результат по остатку (метод средней цены, как на странице «Доходы»). Итог с выплатами включает реализованный результат, выплаты и налоги."
            />
            <MetricCard
              label="Выплаты (нетто)"
              value={formatRub(fund.distributionsNetRub)}
              hint={`брутто ${formatRub(fund.distributionsGrossRub)} · налоги ${formatRub(fund.taxesRub)}`}
              explanation="Полученные выплаты фонда за вычетом налогов, удержанных брокером."
            />
            <MetricCard
              label="XIRR"
              value={formatPercent(fund.xirrFraction)}
              hint="годовых, по операциям фонда"
              explanation="Внутренняя норма доходности по фактическим датам операций с этим фондом. Не считается при владении менее 30 дней."
            />
            <MetricCard
              label="Комиссия (TER)"
              value={formatPercent(fund.totalExpenseFraction)}
              hint="в год, уже учтена в цене и выплатах"
              explanation="Суммарные расходы фонда в год. Они уже вычтены из выплат и учтены в цене пая."
            />
            <MetricCard
              label="Оценка доходности"
              value={formatPercent(outlook.expectedAnnualYieldFraction)}
              hint={outlook.expectedAnnualYieldFraction !== null ? FUND_METHOD_LABEL[outlook.method] : 'недоступна'}
              explanation="Оценка ожидаемой годовой доходности до налога по выбранному методу (см. блок «Прогноз»). Это оценка, а не обещание."
            />
          </MetricGrid>
          <p className="bonds-note">
            <Link to={`/income?asset=fund&focus=${fund.fundId}`} data-testid="fund-income-link">
              Подробно в Доходах
            </Link>{' '}
            — выплаты, налоги и вся история операций по фонду.
          </p>

          <Section title="Доходность по рынку" description="Фактическая доходность пая с учётом выплат — не зависит от вашей цены покупки" data-testid="fund-performance">
            <Group gap="xl" wrap="wrap">
              {(
                [
                  ['1 мес.', fund.performance.m1],
                  ['3 мес.', fund.performance.m3],
                  ['6 мес.', fund.performance.m6],
                  ['12 мес.', fund.performance.m12],
                  ['С первой покупки', fund.performance.sinceFirstBuy],
                ] as const
              ).map(([label, w]) => (
                <div key={label} data-testid={`fund-window-${label}`}>
                  <div className="bonds-eyebrow">{label}</div>
                  <div className="bonds-figure-sm mvv-num">{w ? formatPercent(w.returnFraction) : '—'}</div>
                  {w && w.annualizedFraction !== null && label !== '12 мес.' && (
                    <Text size="xs" c="dimmed">
                      в годовых ≈ {formatPercent(w.annualizedFraction)}
                    </Text>
                  )}
                </div>
              ))}
            </Group>
          </Section>

          <ChartFrame
            title="Цена пая"
            data-testid="fund-price-chart"
            explanation={CHART_EXPLANATIONS.fundPrice}
            empty={priceData.length === 0}
            emptyTitle="Нет истории цены"
            emptyDescription="История цены пая появится после суточного обновления рыночных данных."
          >
            <LineChart data={priceData} margin={t.margin}>
              <CartesianGrid {...t.grid} />
              <XAxis dataKey="label" {...t.xAxis} ticks={pickAxisTicks(priceData.map((d) => d.label), t.maxTicks)} />
              <YAxis {...t.yAxis} tickFormatter={(v: number) => formatRubPrecise(v)} width={84} domain={['auto', 'auto']} />
              <Tooltip
                {...t.tooltip}
                content={({ active, payload }) => {
                  if (!active || !payload?.length) return null;
                  const point = payload[0].payload as (typeof priceData)[number];
                  return (
                    <ChartTooltip
                      title={point.label}
                      rows={[{ label: 'Цена пая', value: formatRubPrecise(point.priceRub), color: chartColors.accent }]}
                    />
                  );
                }}
              />
              <Line {...t.line} dataKey="priceRub" name="Цена пая" stroke={chartColors.accent} />
              {fund.quantity > 0 && fund.avgCostRub > 0 && (
                <ReferenceLine
                  y={fund.avgCostRub}
                  stroke={chartColors.neutral}
                  strokeDasharray="4 4"
                  ifOverflow="extendDomain"
                  label={{ value: 'Ср. цена входа', position: 'insideTopLeft', fontSize: 11, fill: chartColors.tick }}
                />
              )}
              {distributionMarkers.map((m) => (
                <ReferenceDot key={m.key} x={m.label} y={m.priceRub} r={4} fill={chartColors.positive} stroke={chartColors.surface} strokeWidth={2} />
              ))}
            </LineChart>
          </ChartFrame>

          <ChartFrame
            title="Выплаты на пай"
            data-testid="fund-distributions-chart"
            explanation={CHART_EXPLANATIONS.fundDistributions}
            empty={monthlyDistributions.length === 0}
            emptyTitle="Выплат нет"
            emptyDescription="Фонд не выплачивал доход за доступный период (накопительные фонды выплат не делают)."
          >
            <BarChart data={monthlyDistributions} margin={t.margin}>
              <CartesianGrid {...t.grid} />
              <XAxis dataKey="label" {...t.xAxis} ticks={pickAxisTicks(monthlyDistributions.map((d) => d.label), t.maxTicks)} />
              <YAxis {...t.yAxis} width={72} tickFormatter={(v: number) => formatRubCompact(v)} />
              <Tooltip
                {...t.barTooltip}
                content={({ active, payload }) => {
                  if (!active || !payload?.length) return null;
                  const p = payload[0].payload as (typeof monthlyDistributions)[number];
                  return (
                    <ChartTooltip
                      title={p.label}
                      rows={[
                        { label: 'Выплата на пай', value: formatRubPrecise(p.amount), color: chartColors.accent },
                        { label: 'К цене пая', value: formatPercent(p.yieldFraction) },
                      ]}
                    />
                  );
                }}
              />
              <Bar {...t.bar} dataKey="amount" name="Выплата на пай" fill={chartColors.accent} isAnimationActive={false} />
            </BarChart>
          </ChartFrame>

          <Section title="Прогноз (оценка)" description="Ожидаемая доходность — оценка, не обещание" data-testid="fund-outlook">
            <Group justify="space-between" align="flex-end" wrap="wrap" mb="sm">
              <Select
                label="Метод прогноза"
                data={FUND_METHOD_OPTIONS}
                value={detail.settings.forecastMethod}
                allowDeselect={false}
                disabled={settingsSaving}
                onChange={(v) => {
                  if (v && fundId) saveSettings(fundId, v as FundForecastMethod);
                }}
                data-testid="fund-method-select"
              />
              <Badge variant="light" color="gray" data-testid="fund-outlook-method">
                {outlook.expectedAnnualYieldFraction !== null ? `применён: ${FUND_METHOD_LABEL[outlook.method]}` : 'оценка недоступна'}
              </Badge>
            </Group>
            {settingsError && <ErrorState compact error={settingsError} data-testid="fund-settings-error" />}

            {outlook.expectedAnnualYieldFraction === null ? (
              <EmptyState compact title="Оценка недоступна" description={outlook.unavailableReason ?? 'Недостаточно данных.'} data-testid="fund-outlook-unavailable" />
            ) : (
              <>
                <p className="bonds-note" data-testid="fund-outlook-basis">
                  Основа: {outlook.basisLabel}. Уверенность: {FUND_CONFIDENCE_LABEL[outlook.confidence]}.
                </p>
                <Group gap="xl" wrap="wrap">
                  <div>
                    <div className="bonds-eyebrow">Годовая доходность (до налога)</div>
                    <div className="bonds-figure-sm mvv-num" data-testid="fund-outlook-yield">
                      {formatPercent(outlook.expectedAnnualYieldFraction)}
                    </div>
                  </div>
                  <div>
                    <div className="bonds-eyebrow">После налога</div>
                    <div className="bonds-figure-sm mvv-num" data-testid="fund-outlook-after-tax">
                      {formatPercent(outlook.afterTaxYieldFraction)}
                    </div>
                  </div>
                  <div>
                    <div className="bonds-eyebrow">Доход за год (оценка)</div>
                    <div className="bonds-figure-sm mvv-num">{formatRub(outlook.expectedIncome1yRub)}</div>
                  </div>
                  {outlook.expectedMonthlyIncomeRub !== null && (
                    <div>
                      <div className="bonds-eyebrow">Доход в месяц (оценка)</div>
                      <div className="bonds-figure-sm mvv-num">{formatRub(outlook.expectedMonthlyIncomeRub)}</div>
                    </div>
                  )}
                </Group>
              </>
            )}

            {outlook.warnings.length > 0 && (
              <ul className="bonds-footnotes" data-testid="fund-outlook-warnings">
                {outlook.warnings.map((w) => (
                  <li key={w}>{FUND_WARNING_LABEL[w]}</li>
                ))}
              </ul>
            )}
            <p className="bonds-note">{FUND_AFTER_TAX_NOTE}</p>
            <p className="bonds-note" data-testid="fund-key-rate-note">
              {FUND_KEY_RATE_NOTE}
            </p>
          </Section>

          <Section title="Мои операции" padding="none" data-testid="fund-operations">
            {detail.operations.length === 0 ? (
              <EmptyState compact title="Операций нет" description="Операций по фонду в журнале нет." data-testid="fund-operations-empty" />
            ) : (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Дата</Table.Th>
                    <Table.Th>Тип</Table.Th>
                    <Table.Th ta="right">Кол-во</Table.Th>
                    <Table.Th ta="right">Сумма</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {detail.operations.map((op, idx) => (
                    <Table.Tr key={`${op.date}-${idx}`}>
                      <Table.Td>{formatDate(op.date)}</Table.Td>
                      <Table.Td>{FUND_OPERATION_LABEL[op.type] ?? op.type}</Table.Td>
                      <Table.Td ta="right">{op.quantity ?? '—'}</Table.Td>
                      <Table.Td ta="right" c={op.amountRub >= 0 ? 'green' : 'red'}>
                        {formatRub(op.amountRub)}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Section>
        </>
      )}

      <Disclaimer text={detail?.disclaimer} />
    </Page>
  );
}
