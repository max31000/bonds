import { useMemo } from 'react';
import { ChartFrame, ChartTooltip, chartColors, pickAxisTicks, useChartTheme } from '@mvv42/kit/charts';
import { Bar, CartesianGrid, ComposedChart, Legend, Line, ReferenceLine, Tooltip, XAxis, YAxis } from 'recharts';
import type { IncomeMonth } from '../../api/types';
import { CHART_EXPLANATIONS } from '../charts';
import { formatMonthLabel, formatRub, formatRubCompact } from '../../utils/format';

const SERIES = {
  coupons: { name: 'Купоны', color: chartColors.accent },
  distributions: { name: 'Выплаты фондов', color: chartColors.series[1] },
  realized: { name: 'Продажи и погашения', color: chartColors.series[2] },
  accrued: { name: 'НКД (нетто)', color: chartColors.series[3] },
  taxes: { name: 'Налоги', color: chartColors.negative },
  fees: { name: 'Комиссии', color: chartColors.warning },
  net: { name: 'Итог месяца', color: chartColors.text },
} as const;

/**
 * Доход по месяцам: stacked-бары (плюсы вверх, налоги и комиссии вниз от нуля) + линия итога месяца.
 * Только денежные события — бумажная прибыль сюда не входит. Под графиком — среднее за месяц периода.
 */
export function IncomeMonthlyChart({ monthly }: { monthly: IncomeMonth[] }) {
  const t = useChartTheme();
  const data = useMemo(
    () =>
      monthly.map((m) => ({
        month: formatMonthLabel(m.month),
        [SERIES.coupons.name]: m.couponsGrossRub,
        [SERIES.distributions.name]: m.distributionsGrossRub,
        [SERIES.realized.name]: m.realizedPnlRub,
        [SERIES.accrued.name]: m.accruedNetRub,
        [SERIES.taxes.name]: m.taxesRub,
        [SERIES.fees.name]: m.feesRub,
        [SERIES.net.name]: m.netRub,
      })),
    [monthly],
  );
  const avg = monthly.length > 0 ? monthly.reduce((s, m) => s + m.netRub, 0) / monthly.length : 0;

  return (
    <ChartFrame
      title="Доход по месяцам"
      description="Реальные денежные события: что пришло и что ушло в каждом месяце."
      explanation={CHART_EXPLANATIONS.incomeByMonth}
      empty={monthly.length === 0}
      emptyTitle="Нет данных за период"
      data-testid="income-monthly-chart"
      footer={
        monthly.length > 0 ? (
          <p className="bonds-note" data-testid="income-monthly-avg">
            В среднем {formatRub(avg)} в месяц за период ({monthly.length} мес.)
          </p>
        ) : undefined
      }
    >
      <ComposedChart data={data} margin={t.margin} stackOffset="sign">
        <CartesianGrid {...t.grid} />
        <XAxis dataKey="month" {...t.xAxis} ticks={pickAxisTicks(data.map((d) => d.month), t.maxTicks)} />
        <YAxis {...t.yAxis} width={84} tickFormatter={(v: number) => formatRubCompact(v)} />
        <ReferenceLine y={0} stroke={chartColors.axis} />
        <Tooltip
          {...t.barTooltip}
          content={({ active, payload, label }) => {
            if (!active || !payload?.length) return null;
            const rows = payload
              .filter((e) => e.name !== SERIES.net.name && Math.abs(Number(e.value)) >= 0.005)
              .map((e) => ({ label: String(e.name), value: formatRub(Number(e.value)), color: e.color }));
            const net = payload.find((e) => e.name === SERIES.net.name);
            if (net) rows.push({ label: 'Итого за месяц', value: formatRub(Number(net.value)), color: SERIES.net.color });
            return <ChartTooltip title={String(label)} rows={rows} />;
          }}
        />
        <Legend {...t.legend} />
        {(['coupons', 'distributions', 'realized', 'accrued', 'taxes', 'fees'] as const).map((k) => (
          <Bar key={k} {...t.bar} dataKey={SERIES[k].name} stackId="income" fill={SERIES[k].color} isAnimationActive={false} />
        ))}
        <Line {...t.line} dataKey={SERIES.net.name} stroke={SERIES.net.color} dot={{ r: 2 }} isAnimationActive={false} />
      </ComposedChart>
    </ChartFrame>
  );
}
