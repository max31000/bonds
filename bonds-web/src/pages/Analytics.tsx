import { useEffect, useState } from 'react';
import { Title, Stack, Paper, Text, Alert, Loader, Center, Group, SegmentedControl } from '@mantine/core';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
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
import { formatPercent, formatRub, formatSharePercent, formatDate, formatMonthLabel } from '../utils/format';
import type { CompositionSlice, RateScenarioPoint, RateScenarioResponse, ScatterPoint, TrajectoryResponse } from '../api/types';

const PIE_COLORS = [
  'var(--mantine-color-violet-6)',
  'var(--mantine-color-teal-6)',
  'var(--mantine-color-orange-6)',
  'var(--mantine-color-blue-6)',
  'var(--mantine-color-red-5)',
  'var(--mantine-color-yellow-6)',
  'var(--mantine-color-grape-6)',
  'var(--mantine-color-cyan-6)',
];

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

/**
 * T-7/L-1: и ось X scatter, и G-спред должны мерить «срок» одним измерителем — дюрацией Маколея.
 * Раньше точки наносились по модифицированной дюрации, а G-спред считался по Маколею, поэтому
 * визуальное «над/под кривой» не совпадало со знаком G-спреда. Здесь точки строятся по
 * macaulayDuration (нейтральный ключ durationYears), кривая — по своему сроку (termYears).
 * Вынесено в чистую функцию ради юнит-теста (recharts не рендерит ось в jsdom).
 */
export function buildScatterChartData(scatter: { points: ScatterPoint[]; curve: { termYears: number; yield: number }[] }) {
  const points = scatter.points
    .filter((p) => p.effectiveYield != null)
    .map((p) => ({
      ...p,
      durationYears: p.macaulayDuration,
      yieldFraction: p.effectiveYield,
      yieldPercent: (p.effectiveYield ?? 0) * 100,
    }));

  const durations = points.map((p) => p.durationYears).filter(Boolean) as number[];
  const dMin = durations.length > 0 ? Math.min(...durations) : 0;
  const dMax = durations.length > 0 ? Math.max(...durations) : 5;
  const xDomainMin = Math.max(0, dMin - 0.5);
  const xDomainMax = dMax + 0.5;

  const toCurvePoint = (c: { termYears: number; yield: number }) => ({
    durationYears: c.termYears,
    yieldPercent: c.yield * 100,
    yieldFraction: c.yield,
  });

  let curve = scatter.curve
    .filter((c) => c.termYears >= xDomainMin && c.termYears <= xDomainMax)
    .sort((a, b) => a.termYears - b.termYears)
    .map(toCurvePoint);

  // If curve is empty after clipping but scatter.curve is not, include all curve
  if (curve.length === 0 && scatter.curve.length > 0) {
    curve = scatter.curve.slice().sort((a, b) => a.termYears - b.termYears).map(toCurvePoint);
  }

  return { points, curve, xDomainMin, xDomainMax };
}

function ScatterWidget({ scatter }: { scatter: { points: ScatterPoint[]; curve: { termYears: number; yield: number }[]; curveAsOf: string | null } }) {
  const { points: chartPoints, curve: chartCurve, xDomainMin, xDomainMax } = buildScatterChartData(scatter);

  const categories = Array.from(new Set(chartPoints.map(pointCategory)));

  return (
    <Paper withBorder p="md" radius="md" data-testid="scatter-widget">
      <Group justify="space-between" mb="xs">
        <Text fw={600}>Дюрация × доходность</Text>
        {scatter.curveAsOf && (
          <Text size="xs" c="dimmed">
            безрисковая кривая на {formatDate(scatter.curveAsOf)}
          </Text>
        )}
      </Group>

      {chartPoints.length === 0 ? (
        <Text size="sm" c="dimmed" data-testid="scatter-empty">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      ) : (
        <ResponsiveContainer width="100%" height={360}>
          <ScatterChart>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis
              type="number"
              dataKey="durationYears"
              name="Дюрация Маколея"
              unit=" г."
              domain={[xDomainMin, xDomainMax]}
              label={{ value: 'Дюрация Маколея, лет', position: 'insideBottom', offset: -5 }}
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
                return (
                  <Paper withBorder p="xs" radius="sm" shadow="sm">
                    <Text size="xs" fw={600}>
                      {(point as ScatterPoint).name ?? (point as ScatterPoint).issuer ?? `Позиция #${(point as ScatterPoint).positionId}`}
                    </Text>
                    <Text size="xs">Дюрация Маколея: {(point as ScatterPoint).macaulayDuration.toFixed(2)} г.</Text>
                    <Text size="xs">Доходность: {formatPercent(point.yieldFraction)}</Text>
                    <Text size="xs" c="dimmed">
                      {pointCategory(point as ScatterPoint)}
                    </Text>
                  </Paper>
                );
              }}
            />
            <Legend />
            {categories.map((category) => (
              <Scatter
                key={category}
                name={category}
                data={chartPoints.filter((p) => pointCategory(p) === category)}
                fill={CATEGORY_COLOR[category]}
              />
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
    </Paper>
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
    <Paper withBorder p="md" radius="md" data-testid="composition-widget">
      <Group justify="space-between" mb="xs" wrap="wrap">
        <Text fw={600}>Композиция портфеля</Text>
        <SegmentedControl
          size="xs"
          value={view}
          onChange={(v) => setView(v as CompositionView)}
          data={(Object.keys(COMPOSITION_LABEL) as CompositionView[]).map((key) => ({
            value: key,
            label: COMPOSITION_LABEL[key],
          }))}
        />
      </Group>
      <Text size="xs" c="dimmed" mb="sm">
        Всего: {formatRub(composition.totalMarketValueRub)}
      </Text>

      {slices.length === 0 ? (
        <Text size="sm" c="dimmed" data-testid="composition-empty">
          Нет данных для этого разреза.
        </Text>
      ) : (
        <ResponsiveContainer width="100%" height={320}>
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
                <Cell key={s.key} fill={PIE_COLORS[idx % PIE_COLORS.length]} />
              ))}
            </Pie>
            <Tooltip
              formatter={(value, _name, item) => {
                const slice = item?.payload as CompositionSlice | undefined;
                return [
                  `${formatSharePercent(Number(value))} (${formatRub(slice?.marketValueRub)})`,
                  slice?.key ?? '',
                ];
              }}
            />
          </PieChart>
        </ResponsiveContainer>
      )}
    </Paper>
  );
}

function XirrWidget({ xirr }: { xirr: { currentXirr: number | null; history: { date: string; marketValueRub: number; xirr: number }[] } }) {
  return (
    <Paper withBorder p="md" radius="md" data-testid="xirr-widget">
      <Text fw={600} mb="xs">
        Доходность портфеля (XIRR)
      </Text>
      <Text size="xl" fw={700} c="violet" data-testid="xirr-current">
        {formatPercent(xirr.currentXirr)}
      </Text>

      {xirr.history.length === 0 ? (
        <Text size="sm" c="dimmed" mt="sm" data-testid="xirr-empty">
          Недостаточно данных для графика — история копится с первого синка (см. планировщик, этап
          07). Зайдите позже.
        </Text>
      ) : (
        <ResponsiveContainer width="100%" height={300}>
          <LineChart data={xirr.history.map((h) => ({ ...h, dateLabel: formatDate(h.date) }))}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="dateLabel" />
            <YAxis yAxisId="xirr" tickFormatter={(v: number) => formatPercent(v)} />
            <Tooltip formatter={(value) => formatPercent(Number(value))} />
            <Legend />
            <Line yAxisId="xirr" type="monotone" dataKey="xirr" name="XIRR" stroke="var(--mantine-color-violet-6)" />
          </LineChart>
        </ResponsiveContainer>
      )}
    </Paper>
  );
}

function RateScenarioWidget({ rateScenario }: { rateScenario: RateScenarioResponse }) {
  const plus100 = rateScenario.scenarios.find((s) => s.shiftBp === 100);
  const { currentValueRub, rateSensitiveValueRub } = rateScenario;
  // H-1/M-1: Δ относится ко всему портфелю как к базе, но чувствительна к сдвигу только часть с
  // дюрацией — флоатеры/бумаги без дюрации в Δ не входят. Показываем охват честно.
  const hasInsensitivePart = rateSensitiveValueRub < currentValueRub;

  return (
    <Paper withBorder p="md" radius="md" data-testid="rate-scenario-widget">
      <Text fw={600} mb="xs">
        Сценарии ставок
      </Text>

      {rateScenario.scenarios.length === 0 ? (
        <Text size="sm" c="dimmed">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={300}>
            <BarChart data={rateScenario.scenarios}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis
                dataKey="shiftBp"
                label={{ value: 'Сдвиг ключевой ставки, б.п.', position: 'insideBottom', offset: -5 }}
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
                    <Paper p="xs" withBorder>
                      <Text size="xs" fw={500}>{p.shiftBp > 0 ? '+' : ''}{p.shiftBp} б.п.</Text>
                      <Text size="xs">Δ стоимость: {formatRub(p.deltaRub)}</Text>
                      <Text size="xs">Δ%: {p.deltaPercent.toFixed(2)}%</Text>
                    </Paper>
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
    </Paper>
  );
}

function TrajectoryWidget({ trajectory }: { trajectory: TrajectoryResponse }) {
  const chartData = trajectory.withReinvest.map((p, i) => ({
    month: formatMonthLabel(p.month),
    'С реинвестированием': p.portfolioValueRub,
    'Без реинвестирования': trajectory.withoutReinvest[i]?.portfolioValueRub ?? p.portfolioValueRub,
  }));

  return (
    <Paper withBorder p="md" radius="md" data-testid="trajectory-widget">
      <Text fw={600} mb="xs">
        Траектория портфеля
      </Text>

      {trajectory.withReinvest.length === 0 ? (
        <Text size="sm" c="dimmed">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={300}>
            <LineChart data={chartData}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis dataKey="month" label={{ value: 'Месяц', position: 'insideBottom', offset: -5 }} />
              <YAxis
                tickFormatter={(v: number) => formatRub(v)}
                label={{ value: 'Стоимость, ₽', angle: -90, position: 'insideLeft' }}
              />
              <Tooltip formatter={(value) => formatRub(Number(value))} />
              <Legend />
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
    </Paper>
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
