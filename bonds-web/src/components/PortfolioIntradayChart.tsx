import { useEffect, useMemo, useState } from 'react';
import { Paper, Text, Group, SegmentedControl, Center, Loader } from '@mantine/core';
import {
  ResponsiveContainer,
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from 'recharts';
import { fetchPortfolioIntraday } from '../api/live';
import { useLiveStore } from '../store/useLiveStore';
import type { IntradayRange, IntradaySeriesPoint } from '../api/types';
import { formatRub, formatRubCompactRange, formatDateTime } from '../utils/format';

/**
 * Интрадей-виджет «Стоимость портфеля сегодня» (plan/16 часть B) — area-график ряда
 * GET /api/live/portfolio-intraday, переключатель 1д/5д. Переиспускается задачей 18 (дашборд).
 * <p>
 * Ось Y НЕ от нуля (план явно предупреждает: "иначе линия будет плоской" — суточные колебания
 * стоимости портфеля обычно доли процента на фоне абсолютной величины) — домен строится от
 * min/max ряда с отступом.
 * <p>
 * Перезагружает ряд при каждом обновлении useLiveStore (новый тик от useLiveQuotes) — тот же
 * поллинг-цикл, без отдельного собственного таймера.
 */
interface IntradayFetchState {
  points: IntradaySeriesPoint[];
  isLoading: boolean;
  error: string | null;
}

export function PortfolioIntradayChart() {
  const [range, setRange] = useState<IntradayRange>('1d');
  const [state, setState] = useState<IntradayFetchState>({ points: [], isLoading: true, error: null });
  const { points, isLoading, error } = state;

  // Тик поллинга (plan/16 часть B) — новый asOfUtc сигнализирует, что стоит перезапросить ряд,
  // чтобы график рос по мере поступления новых тиков без отдельного таймера.
  const liveAsOfUtc = useLiveStore((s) => s.asOfUtc);

  useEffect(() => {
    let cancelled = false;

    // setState здесь — не "синхронный setState в теле эффекта" (react-hooks/set-state-in-effect):
    // это подписка на внешнюю асинхронную операцию (fetch), setState вызывается только в её
    // callback'ах (then/catch), а не сразу при выполнении эффекта.
    void fetchPortfolioIntraday(range)
      .then((response) => {
        if (!cancelled) setState({ points: response.points, isLoading: false, error: null });
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setState((prev) => ({
            points: prev.points,
            isLoading: false,
            error: err instanceof Error ? err.message : 'Не удалось загрузить график',
          }));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [range, liveAsOfUtc]);

  const chartData = useMemo(
    () =>
      points.map((p) => ({
        tsUtc: p.tsUtc,
        totalMarketValueRub: p.totalMarketValueRub,
        label: formatDateTime(p.tsUtc),
      })),
    [points],
  );

  // min/max сырых значений ряда (без отступа) — нужны и для домена оси (с паддингом ниже), и для
  // range-aware форматтера подписей (formatRubCompactRange, plan/16 §5): на узком диапазоне
  // обычный formatRubCompact схлопывает все деления оси в одну и ту же подпись ("15,5 тыс ₽"
  // четыре раза подряд), formatRubCompactRange переключается на точные рубли в этом случае.
  const valueRange = useMemo((): [number, number] | undefined => {
    if (chartData.length === 0) return undefined;
    const values = chartData.map((d) => d.totalMarketValueRub);
    return [Math.min(...values), Math.max(...values)];
  }, [chartData]);

  const yDomain = useMemo((): [number, number] | undefined => {
    if (!valueRange) return undefined;
    const [min, max] = valueRange;
    if (min === max) {
      // Плоский ряд (один тик/одинаковые значения) — небольшой искусственный отступ, чтобы
      // область графика не схлопывалась в линию без видимой высоты.
      const pad = Math.max(Math.abs(min) * 0.01, 1);
      return [min - pad, max + pad];
    }
    const padding = (max - min) * 0.1;
    return [min - padding, max + padding];
  }, [valueRange]);

  return (
    <Paper withBorder p="md" radius="md" data-testid="portfolio-intraday-chart">
      <Group justify="space-between" mb="xs">
        <Text fw={600}>Стоимость портфеля сегодня</Text>
        <SegmentedControl
          size="xs"
          value={range}
          onChange={(value) => {
            setState((prev) => ({ ...prev, isLoading: true }));
            setRange(value as IntradayRange);
          }}
          data={[
            { label: '1д', value: '1d' },
            { label: '5д', value: '5d' },
          ]}
          data-testid="intraday-range-toggle"
        />
      </Group>

      {isLoading && chartData.length === 0 && (
        <Center py="lg">
          <Loader size="sm" />
        </Center>
      )}

      {!isLoading && error && (
        <Text size="sm" c="red" data-testid="intraday-chart-error">
          {error}
        </Text>
      )}

      {!error && !isLoading && chartData.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="intraday-chart-empty">
          Данных пока нет — график наполнится по мере поступления живых котировок в торговые часы.
        </Text>
      )}

      {chartData.length > 0 && (
        <ResponsiveContainer width="100%" height={220}>
          <AreaChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="label" tick={false} />
            <YAxis
              domain={yDomain}
              tickFormatter={(v: number) =>
                valueRange ? formatRubCompactRange(v, valueRange[0], valueRange[1]) : formatRub(v)
              }
              width={80}
            />
            <Tooltip
              content={({ active, payload }) => {
                if (!active || !payload?.length) return null;
                const point = payload[0].payload as (typeof chartData)[number];
                return (
                  <Paper withBorder p="xs" radius="sm" shadow="sm">
                    <Text size="xs" fw={600}>
                      {formatDateTime(point.tsUtc)}
                    </Text>
                    <Text size="xs">{formatRub(point.totalMarketValueRub)}</Text>
                  </Paper>
                );
              }}
            />
            <Area
              type="monotone"
              dataKey="totalMarketValueRub"
              stroke="var(--mantine-color-violet-6)"
              fill="var(--mantine-color-violet-2)"
              fillOpacity={0.5}
            />
          </AreaChart>
        </ResponsiveContainer>
      )}
    </Paper>
  );
}
