import { useEffect, useState } from 'react';
import { Title, Stack, Text, Alert, Loader, Center, SegmentedControl, Button } from '@mantine/core';
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
  ResponsiveContainer,
  Scatter,
  ScatterChart,
  Tooltip,
  XAxis,
  YAxis,
  ZAxis,
} from 'recharts';
import { useAnalyticsStore } from '../store/useAnalyticsStore';
import { Disclaimer } from '../components/Disclaimer';
import { ChartCard, ChartTooltip, CHART_COLORS, CHART_GRID_PROPS, CHART_HEIGHT, CHART_LEGEND_PROPS, CHART_EXPLANATIONS } from '../components/charts';
import { formatPercent, formatRub, formatRubCompact, formatSharePercent, formatDate, formatMonthLabel } from '../utils/format';
import type { CompositionSlice, RateScenarioPoint, RateScenarioResponse, ScatterPoint, TrajectoryResponse, XirrHistoryPoint } from '../api/types';
import { buildScatterChartData } from '../utils/scatterChartData';

type CompositionView = 'byIssuer' | 'bySector' | 'byCouponType' | 'byDurationBucket';

const COMPOSITION_LABEL: Record<CompositionView, string> = {
  byIssuer: 'По эмитенту',
  bySector: 'По сектору',
  byCouponType: 'По типу купона',
  byDurationBucket: 'По дюрации',
};

/** Категория точки на scatter-графике — используется и для цвета/маркера, и для легенды. */
function pointCategory(p: ScatterPoint): string {
  if (p.dataIncomplete) return 'Неполные данные';
  if (p.isFloater) return 'Флоатер';
  if (p.isIndexed) return 'Индексируемая';
  return 'Обычная';
}

const CATEGORY_COLOR: Record<string, string> = {
  'Обычная': 'var(--mantine-color-violet-6)',
  'Флоатер': 'var(--mantine-color-blue-6)',
  'Индексируемая': 'var(--mantine-color-teal-6)',
  'Неполные данные': 'var(--mantine-color-red-5)',
};

/** Обрезает имя бумаги для подписи точки на графике (plan/18 часть C) — полное имя видно в тултипе. */
function shortLabel(name: string | null | undefined): string {
  if (!name) return '';
  return name.length > 12 ? `${name.slice(0, 12)}…` : name;
}

function ScatterWidget({ scatter }: { scatter: { points: ScatterPoint[]; curve: { termYears: number; yield: number }[]; curveAsOf: string | null } }) {
  const { points: chartPoints, curve: chartCurve, xDomainMin, xDomainMax } = buildScatterChartData(scatter);

  const categories = Array.from(new Set(chartPoints.map(pointCategory)));

  return (
    <ChartCard
      title="Дюрация × доходность"
      data-testid="scatter-widget"
      explanation={CHART_EXPLANATIONS.scatter}
      headerExtra={
        scatter.curveAsOf && (
          <Text size="xs" c="dimmed">
            безрисковая кривая на {formatDate(scatter.curveAsOf)}
          </Text>
        )
      }
    >
      {chartPoints.length === 0 ? (
        <Text size="sm" c="dimmed" data-testid="scatter-empty">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      ) : (
        <ResponsiveContainer width="100%" height={CHART_HEIGHT + 40}>
          <ScatterChart margin={{ top: 8, right: 16, bottom: 24, left: 4 }}>
            <CartesianGrid {...CHART_GRID_PROPS} />
            <XAxis
              type="number"
              dataKey="durationYears"
              name="Дюрация Маколея"
              unit=" г."
              domain={[xDomainMin, xDomainMax]}
              label={{ value: 'Дюрация Маколея, лет', position: 'bottom', offset: 0 }}
            />
            <YAxis
              type="number"
              dataKey="yieldPercent"
              name="Доходность"
              unit="%"
              label={{ value: 'Доходность, %', angle: -90, position: 'insideLeft' }}
            />
            <ZAxis range={[60, 60]} />
            <Tooltip
              cursor={{ strokeDasharray: '3 3' }}
              content={({ payload }) => {
                const point = payload?.[0]?.payload as (typeof chartPoints[0]) | undefined;
                if (!point) return null;
                const p = point as ScatterPoint;
                return (
                  <ChartTooltip
                    title={p.name ?? p.issuer ?? `Позиция #${p.positionId}`}
                    rows={[
                      { label: 'Дюрация Маколея', value: `${p.macaulayDuration.toFixed(2)} г.` },
                      { label: 'Доходность', value: formatPercent(point.yieldFraction) },
                      { label: 'Категория', value: pointCategory(p) },
                    ]}
                  />
                );
              }}
            />
            <Legend {...CHART_LEGEND_PROPS} />
            {categories.map((category) => (
              <Scatter
                key={category}
                name={category}
                data={chartPoints.filter((p) => pointCategory(p) === category)}
                fill={CATEGORY_COLOR[category]}
              >
                <LabelList
                  dataKey="name"
                  position="top"
                  formatter={(value: unknown) => shortLabel(value as string)}
                  style={{ fontSize: 10, fill: 'var(--mantine-color-dimmed)' }}
                />
              </Scatter>
            ))}
            {chartCurve.length > 0 && (
              <Scatter
                name="Безрисковая кривая"
                data={chartCurve}
                line={{ stroke: 'var(--mantine-color-gray-6)' }}
                shape={() => <></>}
                legendType="line"
                fill="var(--mantine-color-gray-6)"
              />
            )}
          </ScatterChart>
        </ResponsiveContainer>
      )}
    </ChartCard>
  );
}

function CompositionWidget({
  composition,
}: {
  composition: { totalMarketValueRub: number } & Record<CompositionView, CompositionSlice[]>;
}) {
  const [view, setView] = useState<CompositionView>('byIssuer');
  const slices = composition[view];

  return (
    <ChartCard
      title="Композиция портфеля"
      data-testid="composition-widget"
      explanation={CHART_EXPLANATIONS.composition}
      controls={
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
    >
      <Text size="xs" c="dimmed" mb="sm">
        Всего: {formatRub(composition.totalMarketValueRub)}
      </Text>

      {slices.length === 0 ? (
        <Text size="sm" c="dimmed" data-testid="composition-empty">
          Нет данных для этого разреза.
        </Text>
      ) : (
        <ResponsiveContainer width="100%" height={CHART_HEIGHT}>
          <PieChart>
            <Pie
              data={slices}
              dataKey="sharePercent"
              nameKey="key"
              cx="50%"
              cy="50%"
              outerRadius={110}
              label={(props) => {
                const slice = props.payload as CompositionSlice | undefined;
                return `${slice?.key ?? ''}: ${formatSharePercent(slice?.sharePercent)}`;
              }}
            >
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
          </PieChart>
        </ResponsiveContainer>
      )}
    </ChartCard>
  );
}

function XirrWidget({ xirr }: { xirr: { currentXirr: number | null; history: XirrHistoryPoint[]; disclaimer?: string } }) {
  const { isBackfilling, runXirrBackfill } = useAnalyticsStore();
  const firstLiveDate = xirr.history[0]?.date;

  const chartData = xirr.history.map((h) => ({ ...h, dateLabel: formatDate(h.date) }));

  return (
    <ChartCard title="Доходность портфеля (XIRR)" data-testid="xirr-widget" explanation={CHART_EXPLANATIONS.xirr}>
      <Text size="xl" fw={700} c="violet" data-testid="xirr-current">
        {formatPercent(xirr.currentXirr)}
      </Text>

      {xirr.history.length === 0 ? (
        <Stack gap="xs" mt="sm">
          <Text size="sm" c="dimmed" data-testid="xirr-empty">
            Недостаточно данных для графика — история копится с первого синка (см. планировщик, этап
            07). Можно восстановить историю ретроспективно по журналу операций и дневным ценам MOEX.
          </Text>
          <Button
            size="xs"
            variant="light"
            w="fit-content"
            loading={isBackfilling}
            onClick={() => void runXirrBackfill()}
            data-testid="xirr-backfill-button"
          >
            Восстановить историю
          </Button>
        </Stack>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={CHART_HEIGHT - 20}>
            <ComposedChart data={chartData}>
              <CartesianGrid {...CHART_GRID_PROPS} />
              <XAxis dataKey="dateLabel" />
              <YAxis yAxisId="xirr" tickFormatter={(v: number) => formatPercent(v)} />
              <YAxis
                yAxisId="value"
                orientation="right"
                tickFormatter={(v: number) => formatRubCompact(v)}
              />
              <Tooltip
                content={({ active, payload }) => {
                  if (!active || !payload?.length) return null;
                  const point = payload[0].payload as (typeof chartData)[number];
                  return (
                    <ChartTooltip
                      title={formatDate(point.date)}
                      rows={[
                        { label: 'XIRR', value: formatPercent(point.xirr) },
                        { label: 'Стоимость', value: formatRub(point.marketValueRub) },
                      ]}
                    />
                  );
                }}
              />
              <Legend {...CHART_LEGEND_PROPS} />
              <Area
                yAxisId="value"
                type="monotone"
                dataKey="marketValueRub"
                name="Стоимость портфеля"
                fill="var(--mantine-color-teal-2)"
                stroke="var(--mantine-color-teal-6)"
                fillOpacity={0.4}
              />
              <Line yAxisId="xirr" type="monotone" dataKey="xirr" name="XIRR" stroke="var(--mantine-color-violet-6)" dot={false} />
            </ComposedChart>
          </ResponsiveContainer>

          <Text size="xs" c="dimmed" mt="sm">
            XIRR — внутренняя норма доходности по фактическим операциям счёта + текущая стоимость.
            {firstLiveDate && (
              <> История до {formatDate(firstLiveDate)} восстановлена по дневным ценам MOEX (приближение);
                для амортизируемых выпусков историческая стоимость оценивается по текущему непогашенному
                номиналу и может быть занижена на прошлых интервалах.</>
            )}
          </Text>
        </>
      )}
    </ChartCard>
  );
}

function RateScenarioWidget({ rateScenario }: { rateScenario: RateScenarioResponse }) {
  const plus100 = rateScenario.scenarios.find((s) => s.shiftBp === 100);
  const { currentValueRub, rateSensitiveValueRub } = rateScenario;
  // H-1/M-1: Δ относится ко всему портфелю как к базе, но чувствительна к сдвигу только часть с
  // дюрацией — флоатеры/бумаги без дюрации в Δ не входят. Показываем охват честно.
  const hasInsensitivePart = rateSensitiveValueRub < currentValueRub;

  return (
    <ChartCard title="Сценарии ставок" data-testid="rate-scenario-widget" explanation={CHART_EXPLANATIONS.rateScenario}>
      {rateScenario.scenarios.length === 0 ? (
        <Text size="sm" c="dimmed">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={CHART_HEIGHT - 20}>
            <BarChart data={rateScenario.scenarios} margin={{ top: 8, right: 16, bottom: 24, left: 4 }}>
              <CartesianGrid {...CHART_GRID_PROPS} />
              <XAxis
                dataKey="shiftBp"
                label={{ value: 'Сдвиг ключевой ставки, б.п.', position: 'bottom', offset: 0 }}
              />
              <YAxis
                unit="%"
                label={{ value: 'Изменение стоимости, %', angle: -90, position: 'insideLeft' }}
              />
              <Tooltip
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
              <Bar dataKey="deltaPercent" name="Δ%">
                {rateScenario.scenarios.map((s, idx) => (
                  <Cell
                    key={idx}
                    fill={s.deltaPercent >= 0 ? 'var(--mantine-color-teal-6)' : 'var(--mantine-color-red-5)'}
                  />
                ))}
              </Bar>
            </BarChart>
          </ResponsiveContainer>

          {plus100 && (
            <Text size="sm" c="dimmed" mt="sm">
              При росте ставок на 100 б.п. стоимость портфеля изменится примерно на {formatRub(Math.abs(plus100.deltaRub))} ({Math.abs(plus100.deltaPercent).toFixed(2)}% от всего портфеля)
              {hasInsensitivePart && (
                <> . Процентно-чувствительная часть: {formatRub(rateSensitiveValueRub)} из {formatRub(currentValueRub)}; флоатеры и бумаги без дюрации к параллельному сдвигу малочувствительны и в Δ не входят.</>
              )}
            </Text>
          )}

          <Disclaimer text={rateScenario.disclaimer} />
        </>
      )}
    </ChartCard>
  );
}

function TrajectoryWidget({ trajectory }: { trajectory: TrajectoryResponse }) {
  const chartData = trajectory.withReinvest.map((p, i) => ({
    month: formatMonthLabel(p.month),
    'С реинвестированием': p.portfolioValueRub,
    'Без реинвестирования': trajectory.withoutReinvest[i]?.portfolioValueRub ?? p.portfolioValueRub,
  }));

  return (
    <ChartCard title="Траектория портфеля" data-testid="trajectory-widget" explanation={CHART_EXPLANATIONS.trajectory}>
      {trajectory.withReinvest.length === 0 ? (
        <Text size="sm" c="dimmed">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={CHART_HEIGHT - 20}>
            <LineChart data={chartData} margin={{ top: 8, right: 16, bottom: 24, left: 4 }}>
              <CartesianGrid {...CHART_GRID_PROPS} />
              <XAxis dataKey="month" label={{ value: 'Месяц', position: 'bottom', offset: 0 }} />
              <YAxis
                tickFormatter={(v: number) => formatRubCompact(v)}
                label={{ value: 'Стоимость, ₽', angle: -90, position: 'insideLeft' }}
              />
              <Tooltip
                content={({ active, payload, label }) => {
                  if (!active || !payload?.length) return null;
                  return (
                    <ChartTooltip
                      title={String(label)}
                      rows={payload.map((entry) => ({
                        label: String(entry.name),
                        value: formatRub(Number(entry.value)),
                        color: entry.color,
                      }))}
                    />
                  );
                }}
              />
              <Legend {...CHART_LEGEND_PROPS} />
              <Line type="monotone" dataKey="С реинвестированием" stroke="var(--mantine-color-teal-6)" dot={false} />
              <Line type="monotone" dataKey="Без реинвестирования" stroke="var(--mantine-color-gray-6)" strokeDasharray="5 5" dot={false} />
            </LineChart>
          </ResponsiveContainer>

          <Text size="sm" c="dimmed" mt="sm">
            Ставка реинвестирования: {formatPercent(trajectory.reinvestRateUsed)}
          </Text>

          <Disclaimer text={trajectory.disclaimer} />
        </>
      )}
    </ChartCard>
  );
}

/**
 * Экран аналитики: scatter «дюрация × доходность», композиция портфеля, кривая XIRR во времени
 * (этап 09b §B.3–B.5). Календарь поступлений — отдельный экран `/cashflow` (§B.2).
 */
export function Analytics() {
  const { scatter, composition, xirr, rateScenario, trajectory, isLoading, error, load } = useAnalyticsStore();

  useEffect(() => {
    load();
  }, [load]);

  return (
    <Stack gap="md">
      <Title order={2}>Аналитика</Title>

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить аналитику" data-testid="analytics-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && scatter && <ScatterWidget scatter={scatter} />}
      {!isLoading && !error && composition && <CompositionWidget composition={composition} />}
      {!isLoading && !error && xirr && <XirrWidget xirr={xirr} />}
      {!isLoading && !error && rateScenario && <RateScenarioWidget rateScenario={rateScenario} />}
      {!isLoading && !error && trajectory && <TrajectoryWidget trajectory={trajectory} />}

      <Disclaimer text={scatter?.disclaimer || composition?.disclaimer || xirr?.disclaimer} />
    </Stack>
  );
}
