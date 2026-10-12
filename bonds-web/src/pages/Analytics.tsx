import { useEffect, useState } from 'react';
import { Button, Loader, SegmentedControl } from '@mantine/core';
import { ErrorState, Page, Section } from '@mvv42/kit';
import {
  ChartFrame,
  ChartTooltip,
  CHART_HEIGHT,
  chartColors,
  pickAxisTicks,
  seriesColor,
  useChartTheme,
} from '@mvv42/kit/charts';
import {
  Area,
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ComposedChart,
  LabelList,
  Legend,
  Line,
  LineChart,
  Pie,
  PieChart,
  Scatter,
  ScatterChart,
  Tooltip,
  XAxis,
  YAxis,
  ZAxis,
} from 'recharts';
import { useAnalyticsStore } from '../store/useAnalyticsStore';
import { Disclaimer } from '../components/Disclaimer';
import { CHART_EXPLANATIONS } from '../components/charts';
import { formatPercent, formatRub, formatRubCompact, formatSharePercent, formatDate, formatMonthLabel } from '../utils/format';
import type { CompositionSlice, RateScenarioPoint, RateScenarioResponse, ScatterPoint, TrajectoryResponse, XirrHistoryPoint } from '../api/types';
import { buildScatterChartData, pointCategory, CATEGORY_COLOR } from '../utils/scatterChartData';

type CompositionView = 'byIssuer' | 'bySector' | 'byCouponType' | 'byDurationBucket';

const COMPOSITION_LABEL: Record<CompositionView, string> = {
  byIssuer: 'Эмитент',
  bySector: 'Сектор',
  byCouponType: 'Купон',
  byDurationBucket: 'Дюрация',
};

/** Полый маркер (только контур) для watchlist-точек — визуально отличает "чужую" бумагу от своих позиций (сплошная заливка). */
function HollowDot(props: { cx?: number; cy?: number; fill?: string }) {
  const { cx, cy, fill } = props;
  if (cx == null || cy == null) return null;
  return <circle cx={cx} cy={cy} r={5} fill="var(--mvv-surface)" stroke={fill} strokeWidth={2} />;
}

/** Обрезает имя бумаги для подписи точки на графике — полное имя видно в тултипе. */
function shortLabel(name: string | null | undefined): string {
  if (!name) return '';
  return name.length > 12 ? `${name.slice(0, 12)}…` : name;
}

/** Подпись оси для графиков, у которых имя оси — часть смысла (дюрация, сдвиг ставки). */
const axisLabelStyle = { fontSize: 12, fill: chartColors.tick };

function ScatterWidget({ scatter }: { scatter: { points: ScatterPoint[]; curve: { termYears: number; yield: number }[]; curveAsOf: string | null } }) {
  const { points: chartPoints, curve: chartCurve, xDomainMin, xDomainMax } = buildScatterChartData(scatter);
  const t = useChartTheme({ height: CHART_HEIGHT + 60 });

  const categories = Array.from(new Set(chartPoints.map(pointCategory)));

  return (
    <ChartFrame
      title="Дюрация × доходность"
      description={`Доходность, % годовых${scatter.curveAsOf ? ` · безрисковая кривая на ${formatDate(scatter.curveAsOf)}` : ''}`}
      data-testid="scatter-widget"
      explanation={CHART_EXPLANATIONS.scatter}
      height={CHART_HEIGHT + 60}
      empty={chartPoints.length === 0}
      emptyTitle={<span data-testid="scatter-empty">Пока нет данных</span>}
      emptyDescription="Данные появятся после синхронизации с брокерским счётом."
    >
      <ScatterChart margin={{ ...t.margin, bottom: 24, left: 8 }}>
        <CartesianGrid {...t.grid} vertical />
        <XAxis
          type="number"
          dataKey="durationYears"
          name="Дюрация Маколея"
          unit=" г."
          domain={[xDomainMin, xDomainMax]}
          {...t.xAxis}
          label={{ value: 'Дюрация Маколея, лет', position: 'bottom', offset: 6, ...axisLabelStyle }}
        />
        <YAxis
          type="number"
          dataKey="yieldPercent"
          name="Доходность"
          unit="%"
          {...t.yAxis}
          width={44}
        />
        <ZAxis range={[64, 64]} />
        <Tooltip
          {...t.tooltip}
          cursor={{ stroke: chartColors.axis, strokeDasharray: '3 3' }}
          content={({ payload }) => {
            const point = payload?.[0]?.payload as (typeof chartPoints)[0] | undefined;
            if (!point) return null;
            const p = point as ScatterPoint;
            return (
              <ChartTooltip
                title={p.name ?? p.issuer ?? `Позиция #${p.positionId}`}
                rows={[
                  { label: 'Дюрация Маколея', value: `${p.macaulayDuration.toFixed(2)} г.` },
                  { label: 'Доходность', value: formatPercent(point.yieldFraction) },
                  { label: 'Категория', value: pointCategory(p), color: CATEGORY_COLOR[pointCategory(p)] },
                ]}
              />
            );
          }}
        />
        <Legend {...t.legend} />
        {categories.map((category) => (
          <Scatter
            key={category}
            name={category}
            data={chartPoints.filter((p) => pointCategory(p) === category)}
            fill={CATEGORY_COLOR[category]}
            shape={category === 'Watchlist' ? HollowDot : undefined}
            isAnimationActive={false}
          >
            <LabelList
              dataKey="name"
              position="top"
              formatter={(value: unknown) => shortLabel(value as string)}
              style={{ fontSize: 10, fill: chartColors.tick }}
            />
          </Scatter>
        ))}
        {chartCurve.length > 0 && (
          <Scatter
            name="Безрисковая кривая"
            data={chartCurve}
            line={{ stroke: chartColors.neutral, strokeWidth: 1.5 }}
            shape={() => <></>}
            legendType="line"
            fill={chartColors.neutral}
            isAnimationActive={false}
          />
        )}
      </ScatterChart>
    </ChartFrame>
  );
}

function CompositionWidget({
  composition,
}: {
  composition: { totalMarketValueRub: number } & Record<CompositionView, CompositionSlice[]>;
}) {
  const [view, setView] = useState<CompositionView>('byIssuer');
  const slices = composition[view];
  const t = useChartTheme();

  return (
    <ChartFrame
      title="Композиция портфеля"
      description={`Всего: ${formatRub(composition.totalMarketValueRub)}`}
      data-testid="composition-widget"
      explanation={CHART_EXPLANATIONS.composition}
      actions={
        <SegmentedControl
          size="xs"
          value={view}
          onChange={(v) => setView(v as CompositionView)}
          data={(Object.keys(COMPOSITION_LABEL) as CompositionView[]).map((key) => ({
            value: key,
            label: COMPOSITION_LABEL[key],
          }))}
        />
      }
      empty={slices.length === 0}
      emptyTitle={<span data-testid="composition-empty">Нет данных</span>}
      emptyDescription="Нет данных для этого разреза."
    >
      <PieChart>
        <Pie
          data={slices}
          dataKey="sharePercent"
          nameKey="key"
          cx={t.isCompact ? '50%' : '32%'}
          cy={t.isCompact ? '42%' : '50%'}
          outerRadius={t.isCompact ? '70%' : '74%'}
          innerRadius={t.isCompact ? '46%' : '50%'}
          paddingAngle={1}
          stroke="var(--mvv-surface)"
          strokeWidth={2}
          isAnimationActive={false}
        >
          {slices.map((s, idx) => (
            <Cell key={s.key} fill={seriesColor(idx)} />
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
          formatter={(value: unknown, entry: { payload?: unknown }) => {
            const slice = entry.payload as CompositionSlice | undefined;
            return (
              <span style={{ color: chartColors.text }}>
                {String(value)}
                {slice ? <span style={{ color: chartColors.tick }}> · {formatSharePercent(slice.sharePercent)}</span> : null}
              </span>
            );
          }}
        />
      </PieChart>
    </ChartFrame>
  );
}

function XirrWidget({ xirr }: { xirr: { currentXirr: number | null; history: XirrHistoryPoint[]; disclaimer?: string } }) {
  const { isBackfilling, runXirrBackfill } = useAnalyticsStore();
  const firstLiveDate = xirr.history[0]?.date;
  const t = useChartTheme({ height: CHART_HEIGHT - 20 });

  const chartData = xirr.history.map((h) => ({ ...h, dateLabel: formatDate(h.date) }));

  return (
    <ChartFrame
      title="Доходность портфеля (XIRR)"
      data-testid="xirr-widget"
      explanation={CHART_EXPLANATIONS.xirr}
      height={CHART_HEIGHT - 20}
      actions={
        <span className="bonds-header-figure">
          <span className="bonds-eyebrow">сейчас</span>
          <span className="bonds-figure-sm mvv-num" data-testid="xirr-current">
            {formatPercent(xirr.currentXirr)}
          </span>
        </span>
      }
      empty={xirr.history.length === 0}
      emptyTitle={<span data-testid="xirr-empty">Недостаточно данных для графика</span>}
      emptyDescription="История копится с первого синка. Можно восстановить её ретроспективно по журналу операций и дневным ценам MOEX."
      emptyAction={
        <Button
          size="xs"
          variant="default"
          loading={isBackfilling}
          onClick={() => void runXirrBackfill()}
          data-testid="xirr-backfill-button"
        >
          Восстановить историю
        </Button>
      }
      footer={
        <p className="bonds-note">
          XIRR — внутренняя норма доходности по фактическим операциям счёта + текущая стоимость.
          {firstLiveDate && (
            <>
              {' '}
              История до {formatDate(firstLiveDate)} восстановлена по дневным ценам MOEX (приближение); для
              амортизируемых выпусков историческая стоимость оценивается по текущему непогашенному номиналу и может
              быть занижена на прошлых интервалах.
            </>
          )}
        </p>
      }
    >
      <ComposedChart data={chartData} margin={t.margin}>
        <CartesianGrid {...t.grid} />
        <XAxis dataKey="dateLabel" {...t.xAxis} ticks={pickAxisTicks(chartData.map((d) => d.dateLabel), t.maxTicks)} />
        <YAxis yAxisId="xirr" {...t.yAxis} tickFormatter={(v: number) => formatPercent(v)} />
        <YAxis yAxisId="value" orientation="right" {...t.yAxis} width={84} tickFormatter={(v: number) => formatRubCompact(v)} />
        <Tooltip
          {...t.tooltip}
          content={({ active, payload }) => {
            if (!active || !payload?.length) return null;
            const point = payload[0].payload as (typeof chartData)[number];
            return (
              <ChartTooltip
                title={formatDate(point.date)}
                rows={[
                  { label: 'XIRR', value: formatPercent(point.xirr), color: chartColors.accent },
                  { label: 'Стоимость', value: formatRub(point.marketValueRub), color: seriesColor(2) },
                ]}
              />
            );
          }}
        />
        <Legend {...t.legend} />
        <Area
          {...t.area}
          yAxisId="value"
          dataKey="marketValueRub"
          name="Стоимость портфеля"
          stroke={seriesColor(2)}
          fill={seriesColor(2)}
        />
        <Line {...t.line} yAxisId="xirr" dataKey="xirr" name="XIRR" stroke={chartColors.accent} />
      </ComposedChart>
    </ChartFrame>
  );
}

function RateScenarioWidget({ rateScenario }: { rateScenario: RateScenarioResponse }) {
  const plus100 = rateScenario.scenarios.find((s) => s.shiftBp === 100);
  const { currentValueRub, rateSensitiveValueRub } = rateScenario;
  // H-1/M-1: Δ относится ко всему портфелю как к базе, но чувствительна к сдвигу только часть с
  // дюрацией — флоатеры/бумаги без дюрации в Δ не входят. Показываем охват честно.
  const hasInsensitivePart = rateSensitiveValueRub < currentValueRub;
  const t = useChartTheme({ height: CHART_HEIGHT - 20 });

  return (
    <ChartFrame
      title="Сценарии ставок"
      description="Изменение стоимости портфеля, % — при параллельном сдвиге ключевой ставки"
      data-testid="rate-scenario-widget"
      explanation={CHART_EXPLANATIONS.rateScenario}
      height={CHART_HEIGHT - 20}
      empty={rateScenario.scenarios.length === 0}
      emptyDescription="Данные появятся после синхронизации с брокерским счётом."
      footer={
        <>
          {plus100 && (
            <p className="bonds-note">
              При росте ставок на 100 б.п. стоимость портфеля изменится примерно на{' '}
              {formatRub(Math.abs(plus100.deltaRub))} ({Math.abs(plus100.deltaPercent).toFixed(2)}% от всего портфеля)
              {hasInsensitivePart && (
                <>
                  . Процентно-чувствительная часть: {formatRub(rateSensitiveValueRub)} из {formatRub(currentValueRub)};
                  флоатеры и бумаги без дюрации к параллельному сдвигу малочувствительны и в Δ не входят.
                </>
              )}
            </p>
          )}
          <Disclaimer text={rateScenario.disclaimer} />
        </>
      }
    >
      <BarChart data={rateScenario.scenarios} margin={{ ...t.margin, bottom: 24, left: 8 }}>
        <CartesianGrid {...t.grid} />
        <XAxis
          dataKey="shiftBp"
          {...t.xAxis}
          tickFormatter={(v: number) => `${v > 0 ? '+' : ''}${v}`}
          label={{ value: 'Сдвиг ключевой ставки, б.п.', position: 'bottom', offset: 6, ...axisLabelStyle }}
        />
        <YAxis {...t.yAxis} width={44} unit="%" />
        <Tooltip
          {...t.barTooltip}
          content={({ active, payload }) => {
            if (!active || !payload?.length) return null;
            const p = payload[0].payload as RateScenarioPoint;
            return (
              <ChartTooltip
                title={`${p.shiftBp > 0 ? '+' : ''}${p.shiftBp} б.п.`}
                rows={[
                  { label: 'Δ стоимость', value: formatRub(p.deltaRub) },
                  { label: 'Δ%', value: `${p.deltaPercent.toFixed(2)}%` },
                ]}
              />
            );
          }}
        />
        <Bar {...t.bar} dataKey="deltaPercent" name="Δ%" isAnimationActive={false}>
          {rateScenario.scenarios.map((s, idx) => (
            <Cell key={idx} fill={s.deltaPercent >= 0 ? chartColors.positive : chartColors.negative} />
          ))}
        </Bar>
      </BarChart>
    </ChartFrame>
  );
}

function TrajectoryWidget({ trajectory }: { trajectory: TrajectoryResponse }) {
  const t = useChartTheme({ height: CHART_HEIGHT - 20 });
  const chartData = trajectory.withReinvest.map((p, i) => ({
    month: formatMonthLabel(p.month),
    'С реинвестированием': p.portfolioValueRub,
    'Без реинвестирования': trajectory.withoutReinvest[i]?.portfolioValueRub ?? p.portfolioValueRub,
  }));

  return (
    <ChartFrame
      title="Траектория портфеля"
      description={`Ставка реинвестирования: ${formatPercent(trajectory.reinvestRateUsed)}`}
      data-testid="trajectory-widget"
      explanation={CHART_EXPLANATIONS.trajectory}
      height={CHART_HEIGHT - 20}
      empty={trajectory.withReinvest.length === 0}
      emptyDescription="Данные появятся после синхронизации с брокерским счётом."
      footer={<Disclaimer text={trajectory.disclaimer} />}
    >
      <LineChart data={chartData} margin={t.margin}>
        <CartesianGrid {...t.grid} />
        <XAxis dataKey="month" {...t.xAxis} ticks={pickAxisTicks(chartData.map((d) => d.month), t.maxTicks)} />
        <YAxis {...t.yAxis} width={84} tickFormatter={(v: number) => formatRubCompact(v)} />
        <Tooltip
          {...t.tooltip}
          content={<ChartTooltip valueFormatter={(v) => formatRub(Number(v))} />}
        />
        <Legend {...t.legend} />
        <Line {...t.line} dataKey="С реинвестированием" stroke={chartColors.accent} />
        <Line {...t.line} dataKey="Без реинвестирования" stroke={chartColors.neutral} strokeDasharray="5 5" />
      </LineChart>
    </ChartFrame>
  );
}

/**
 * Экран аналитики: scatter «дюрация × доходность», композиция портфеля, кривая XIRR во времени,
 * сценарии ставок и траектория (этап 09b §B.3–B.5). Календарь поступлений — `/cashflow` (§B.2).
 */
export function Analytics() {
  const { scatter, composition, xirr, rateScenario, trajectory, isLoading, error, load } = useAnalyticsStore();

  useEffect(() => {
    load();
  }, [load]);

  return (
    <Page title="Аналитика" description="Риск-доходность, состав и чувствительность портфеля к ставкам" width="wide">
      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState
            title="Не удалось загрузить аналитику"
            error={error}
            onRetry={() => load()}
            data-testid="analytics-error"
          />
        </Section>
      )}

      {!isLoading && !error && scatter && <ScatterWidget scatter={scatter} />}
      <div className="bonds-grid-2">
        {!isLoading && !error && composition && <CompositionWidget composition={composition} />}
        {!isLoading && !error && xirr && <XirrWidget xirr={xirr} />}
      </div>
      <div className="bonds-grid-2">
        {!isLoading && !error && rateScenario && <RateScenarioWidget rateScenario={rateScenario} />}
        {!isLoading && !error && trajectory && <TrajectoryWidget trajectory={trajectory} />}
      </div>

      <Disclaimer text={scatter?.disclaimer || composition?.disclaimer || xirr?.disclaimer} />
    </Page>
  );
}
