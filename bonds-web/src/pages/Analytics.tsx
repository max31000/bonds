import { useEffect, useState } from 'react';
import { Title, Stack, Paper, Text, Alert, Loader, Center, Group, SegmentedControl } from '@mantine/core';
import {
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
import { formatPercent, formatRub, formatSharePercent, formatDate } from '../utils/format';
import type { CompositionSlice, ScatterPoint } from '../api/types';

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

function ScatterWidget({ scatter }: { scatter: { points: ScatterPoint[]; curve: { termYears: number; yield: number }[]; curveAsOf: string | null } }) {
  const categories = Array.from(new Set(scatter.points.map(pointCategory)));

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

      {scatter.points.length === 0 ? (
        <Text size="sm" c="dimmed" data-testid="scatter-empty">
          Нет позиций с рассчитанной дюрацией и доходностью.
        </Text>
      ) : (
        <ResponsiveContainer width="100%" height={360}>
          <ScatterChart>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis
              type="number"
              dataKey="modifiedDuration"
              name="Дюрация"
              unit=" г."
              label={{ value: 'Модифицированная дюрация, лет', position: 'insideBottom', offset: -5 }}
            />
            <YAxis
              type="number"
              dataKey="effectiveYield"
              name="Доходность"
              unit="%"
              label={{ value: 'Доходность, %', angle: -90, position: 'insideLeft' }}
            />
            <ZAxis range={[60, 60]} />
            <Tooltip
              cursor={{ strokeDasharray: '3 3' }}
              content={({ payload }) => {
                const point = payload?.[0]?.payload as ScatterPoint | undefined;
                if (!point) return null;
                return (
                  <Paper withBorder p="xs" radius="sm" shadow="sm">
                    <Text size="xs" fw={600}>
                      {point.issuer ?? `Позиция #${point.positionId}`}
                    </Text>
                    <Text size="xs">Дюрация: {point.modifiedDuration.toFixed(2)} г.</Text>
                    <Text size="xs">Доходность: {formatPercent(point.effectiveYield)}</Text>
                    <Text size="xs" c="dimmed">
                      {pointCategory(point)}
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
                data={scatter.points.filter((p) => pointCategory(p) === category)}
                fill={CATEGORY_COLOR[category]}
              />
            ))}
            {scatter.curve.length > 0 && (
              <Scatter
                name="Безрисковая кривая"
                data={scatter.curve.map((c) => ({ modifiedDuration: c.termYears, effectiveYield: c.yield }))}
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

/**
 * Экран аналитики: scatter «дюрация × доходность», композиция портфеля, кривая XIRR во времени
 * (этап 09b §B.3–B.5). Календарь поступлений — отдельный экран `/cashflow` (§B.2).
 */
export function Analytics() {
  const { scatter, composition, xirr, isLoading, error, load } = useAnalyticsStore();

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

      <Disclaimer text={scatter?.disclaimer || composition?.disclaimer || xirr?.disclaimer} />
    </Stack>
  );
}
