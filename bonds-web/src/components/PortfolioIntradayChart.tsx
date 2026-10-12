import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { SegmentedControl } from '@mantine/core';
import { ChartFrame, ChartTooltip, CHART_HEIGHT_COMPACT, chartColors, useChartTheme } from '@mvv42/kit/charts';
import {
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

export function PortfolioIntradayChart({ explanation }: { explanation?: ReactNode } = {}) {
  const t = useChartTheme();
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
    <ChartFrame
      title="Стоимость облигаций"
      data-testid="portfolio-intraday-chart"
      explanation={explanation}
      height={CHART_HEIGHT_COMPACT}
      actions={
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
      }
      loading={isLoading && chartData.length === 0}
      error={!isLoading && error ? error : undefined}
      empty={!error && !isLoading && chartData.length === 0}
      emptyTitle={<span data-testid="intraday-chart-empty">Данных пока нет</span>}
      emptyDescription="График наполнится по мере поступления живых котировок в торговые часы."
    >
      <AreaChart data={chartData} margin={t.margin}>
        <defs>
          <linearGradient id="bonds-intraday-fill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={chartColors.accent} stopOpacity={0.22} />
            <stop offset="100%" stopColor={chartColors.accent} stopOpacity={0} />
          </linearGradient>
        </defs>
        <CartesianGrid {...t.grid} />
        <XAxis dataKey="label" {...t.xAxis} tick={false} height={4} />
        <YAxis
          {...t.yAxis}
          width={84}
          domain={yDomain}
          tickFormatter={(v: number) =>
            valueRange ? formatRubCompactRange(v, valueRange[0], valueRange[1]) : formatRub(v)
          }
        />
        <Tooltip
          {...t.tooltip}
          content={({ active, payload }) => {
            if (!active || !payload?.length) return null;
            const point = payload[0].payload as (typeof chartData)[number];
            return (
              <ChartTooltip
                title={formatDateTime(point.tsUtc)}
                rows={[{ label: 'Стоимость', value: formatRub(point.totalMarketValueRub), color: chartColors.accent }]}
              />
            );
          }}
        />
        <Area
          {...t.area}
          dataKey="totalMarketValueRub"
          name="Стоимость"
          stroke={chartColors.accent}
          fill="url(#bonds-intraday-fill)"
          fillOpacity={1}
        />
      </AreaChart>
    </ChartFrame>
  );
}
