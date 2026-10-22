import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Analytics } from './Analytics';
import { buildScatterChartData } from '../utils/scatterChartData';
import { useAnalyticsStore } from '../store/useAnalyticsStore';
import type { CompositionResponse, RateScenarioResponse, ScatterResponse, TrajectoryResponse, XirrResponse } from '../api/types';

function renderAnalytics(initialEntry = '/analytics') {
  return render(
    <MantineProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Analytics />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const baseScatter: ScatterResponse = {
  points: [
    {
      positionId: 1,
      instrumentId: 10,
      name: null,
      issuer: 'Минфин РФ',
      modifiedDuration: 3.2,
      macaulayDuration: 3.4,
      effectiveYield: 0.125,
      yieldKind: 'Ytm',
      isFloater: false,
      isIndexed: false,
      isEstimated: false,
      dataIncomplete: false,
      isWatchlist: false,
    },
    {
      positionId: 2,
      instrumentId: 11,
      name: null,
      issuer: 'РЖД',
      modifiedDuration: 1.5,
      macaulayDuration: 1.6,
      effectiveYield: 0.094,
      yieldKind: 'CurrentYield',
      isFloater: true,
      isIndexed: false,
      isEstimated: true,
      dataIncomplete: false,
      isWatchlist: false,
    },
  ],
  curve: [
    { termYears: 0.25, yield: 0.10 },
    { termYears: 5, yield: 0.11 },
  ],
  curveAsOf: '2026-06-25',
  disclaimer: '',
};

const baseComposition: CompositionResponse = {
  totalMarketValueRub: 200000,
  byIssuer: [{ key: 'Минфин РФ', marketValueRub: 105000, sharePercent: 52.5 }],
  bySector: [{ key: 'ОФЗ', marketValueRub: 105000, sharePercent: 52.5 }],
  byCouponType: [{ key: 'Fixed', marketValueRub: 105000, sharePercent: 52.5 }],
  byDurationBucket: [{ key: '1-3 года', marketValueRub: 105000, sharePercent: 52.5 }],
  disclaimer: '',
};

const baseRateScenario: RateScenarioResponse = {
  currentValueRub: 200000,
  rateSensitiveValueRub: 200000,
  scenarios: [
    { shiftBp: -200, newValueRub: 212000, deltaRub: 12000, deltaPercent: 6 },
    { shiftBp: -100, newValueRub: 206000, deltaRub: 6000, deltaPercent: 3 },
    { shiftBp: -50, newValueRub: 203000, deltaRub: 3000, deltaPercent: 1.5 },
    { shiftBp: 0, newValueRub: 200000, deltaRub: 0, deltaPercent: 0 },
    { shiftBp: 50, newValueRub: 197000, deltaRub: -3000, deltaPercent: -1.5 },
    { shiftBp: 100, newValueRub: 194000, deltaRub: -6000, deltaPercent: -3 },
    { shiftBp: 200, newValueRub: 188000, deltaRub: -12000, deltaPercent: -6 },
  ],
  disclaimer: '',
};

const baseXirr: XirrResponse = {
  currentXirr: 0.132,
  history: [
    { date: '2026-06-01', marketValueRub: 200000, investedRub: 180000, xirr: 0.128 },
    { date: '2026-06-25', marketValueRub: 205000, investedRub: 180000, xirr: 0.132 },
  ],
  disclaimer: '',
};

const baseTrajectory: TrajectoryResponse = {
  initialValueRub: 200000,
  withReinvest: [
    { month: '2026-07', portfolioValueRub: 201010, cumulativeIncomeRub: 1000 },
    { month: '2026-08', portfolioValueRub: 202030, cumulativeIncomeRub: 2000 },
  ],
  withoutReinvest: [
    { month: '2026-07', portfolioValueRub: 201000, cumulativeIncomeRub: 1000 },
    { month: '2026-08', portfolioValueRub: 202000, cumulativeIncomeRub: 2000 },
  ],
  reinvestRateUsed: 0.12,
  disclaimer: '',
};

describe('Analytics', () => {
  beforeEach(() => {
    useAnalyticsStore.setState({
      scatter: null,
      composition: null,
      xirr: null,
      rateScenario: null,
      trajectory: null,
      isLoading: false,
      error: null,
      isBackfilling: false,
    });
  });

  it('renders the scatter, composition, and xirr widgets with data', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
    expect(screen.getByTestId('composition-widget')).toBeInTheDocument();
    expect(screen.getByTestId('xirr-widget')).toBeInTheDocument();
    expect(screen.getByTestId('xirr-current')).toHaveTextContent('13.20%');
  });

  it('renders the scatter widget without crashing when a watchlist point is present', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () =>
        HttpResponse.json({
          ...baseScatter,
          points: [
            ...baseScatter.points,
            {
              positionId: 0,
              instrumentId: 999,
              name: 'Чужая бумага',
              issuer: 'Другой эмитент',
              modifiedDuration: 4.0,
              macaulayDuration: 4.2,
              effectiveYield: 0.16,
              yieldKind: 'Ytm',
              isFloater: false,
              isIndexed: false,
              isEstimated: false,
              dataIncomplete: false,
              isWatchlist: true,
            },
          ],
        }),
      ),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
  });

  it('opens the explanation popover for the scatter widget', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    // «?» графика — кнопка ChartFrame кита («Как читать график») внутри виджета.
    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
    const explain = within(screen.getByTestId('scatter-widget')).getByRole('button', { name: 'Как читать график' });

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(explain, { pointerEventsCheck: 0 });

    await waitFor(() => expect(screen.getByText(/Серая линия — безрисковая кривая ОФЗ/)).toBeInTheDocument());
  });

  it('uses a neutral curve label and never renders the MOEX trademark name', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
    expect(screen.getByText(/безрисковая кривая/i)).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/MOEX GCURVE/i);
    expect(document.body.textContent).not.toMatch(/КБД Московской Биржи/i);
  });

  it('shows an empty state for the xirr widget when history is empty', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () =>
        HttpResponse.json({ currentXirr: null, history: [], disclaimer: '' }),
      ),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('xirr-empty')).toBeInTheDocument());
  });

  it('renders XIRR history as a two-axis chart with value + xirr when history is present', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('xirr-widget')).toBeInTheDocument());
    expect(screen.queryByTestId('xirr-empty')).not.toBeInTheDocument();
    expect(screen.queryByTestId('xirr-backfill-button')).not.toBeInTheDocument();
    expect(screen.getByText(/восстановлена по дневным ценам MOEX/i)).toBeInTheDocument();
  });

  it('runs backfill and reloads XIRR history when the empty-state button is clicked', async () => {
    let backfillCalled = false;
    let xirrCallCount = 0;

    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => {
        xirrCallCount += 1;
        return xirrCallCount === 1
          ? HttpResponse.json({ currentXirr: null, history: [], disclaimer: '' })
          : HttpResponse.json(baseXirr);
      }),
      http.post('*/api/analytics/xirr/backfill', () => {
        backfillCalled = true;
        return HttpResponse.json({ pointsWritten: 2 });
      }),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('xirr-backfill-button')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('xirr-backfill-button'));

    await waitFor(() => expect(screen.getByTestId('xirr-current')).toHaveTextContent('13.20%'));
    expect(backfillCalled).toBe(true);
    expect(xirrCallCount).toBe(2);
  });

  it('shows an empty state for the scatter widget when there are no points', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () =>
        HttpResponse.json({ points: [], curve: [], curveAsOf: null, disclaimer: '' }),
      ),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-empty')).toBeInTheDocument());
  });

  it('shows an empty state for a composition slice with no data', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () =>
        HttpResponse.json({
          totalMarketValueRub: 0,
          byIssuer: [],
          bySector: [],
          byCouponType: [],
          byDurationBucket: [],
          disclaimer: '',
        }),
      ),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('composition-empty')).toBeInTheDocument());
  });

  it('shows an error state without crashing when a request fails', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('analytics-error')).toBeInTheDocument());
  });

  it('calculates X-axis domain correctly with multiple portfolio points', async () => {
    const scatterWithMultiplePoints: ScatterResponse = {
      points: [
        { ...baseScatter.points[0], modifiedDuration: 1.5 },
        { ...baseScatter.points[1], modifiedDuration: 2.5 },
        { ...baseScatter.points[0], positionId: 3, modifiedDuration: 3.0 },
      ],
      curve: baseScatter.curve,
      curveAsOf: baseScatter.curveAsOf,
      disclaimer: '',
    };

    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(scatterWithMultiplePoints)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
    expect(screen.getByTestId('scatter-widget')).toBeInTheDocument();
  });

  it('calculates X-axis domain correctly with single portfolio point', async () => {
    const scatterWithSinglePoint: ScatterResponse = {
      points: [{ ...baseScatter.points[0], modifiedDuration: 2.0 }],
      curve: baseScatter.curve,
      curveAsOf: baseScatter.curveAsOf,
      disclaimer: '',
    };

    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(scatterWithSinglePoint)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
    expect(screen.getByTestId('scatter-widget')).toBeInTheDocument();
  });

  it('displays yield percentages correctly (not fractions)', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('scatter-widget')).toBeInTheDocument());
    expect(screen.getByTestId('scatter-widget')).toBeInTheDocument();
  });

  it('shows rate scenario widget when data is present', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/analytics/rate-scenario', () => HttpResponse.json(baseRateScenario)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('rate-scenario-widget')).toBeInTheDocument());
  });

  it('shows trajectory widget when data is present', async () => {
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/analytics/rate-scenario', () => HttpResponse.json(baseRateScenario)),
      http.get('*/api/analytics/trajectory', () => HttpResponse.json(baseTrajectory)),
    );

    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('trajectory-widget')).toBeInTheDocument());
  });
});

describe('Analytics — фонды (задача 57)', () => {
  const withFundsComposition: CompositionResponse = {
    ...baseComposition,
    byIssuer: [
      { key: 'Минфин РФ', marketValueRub: 105000, sharePercent: 52.5 },
      { key: 'Фонд: Облигации ОФЗ', marketValueRub: 95000, sharePercent: 47.5 },
    ],
    byAssetClass: [
      { key: 'Облигации', marketValueRub: 105000, sharePercent: 52.5 },
      { key: 'Фонды', marketValueRub: 95000, sharePercent: 47.5 },
    ],
    fundsIncluded: true,
    hasFunds: true,
  };

  const withFundsScenario: RateScenarioResponse = {
    ...baseRateScenario,
    fundsIncluded: true,
    hasFunds: true,
    funds: [
      { fundId: 5, ticker: 'TOFZ', name: 'Облигации ОФЗ', marketValueRub: 95000, modifiedDurationYears: 4.2, durationSource: 'index:RGBI', deltaAtPlus100bpRub: -3990 },
      { fundId: 7, ticker: 'TPAY', name: 'Пассивный доход', marketValueRub: 10000, modifiedDurationYears: null, durationSource: 'unknown', deltaAtPlus100bpRub: null },
    ],
  };

  let urls: URL[];

  beforeEach(() => {
    urls = [];
    useAnalyticsStore.setState({ scatter: null, composition: null, xirr: null, rateScenario: null, trajectory: null, isLoading: false, error: null, isBackfilling: false });
    server.use(
      http.get('*/api/analytics/scatter', () => HttpResponse.json(baseScatter)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/analytics/composition', ({ request }) => {
        const url = new URL(request.url);
        urls.push(url);
        return HttpResponse.json(
          url.searchParams.get('includeFunds') === 'false'
            ? { ...baseComposition, byAssetClass: [], fundsIncluded: false, hasFunds: true }
            : withFundsComposition,
        );
      }),
      http.get('*/api/analytics/rate-scenario', ({ request }) => {
        const url = new URL(request.url);
        urls.push(url);
        return HttpResponse.json(
          url.searchParams.get('includeFunds') === 'false'
            ? { ...baseRateScenario, fundsIncluded: false, hasFunds: true, funds: [] }
            : withFundsScenario,
        );
      }),
    );
  });

  it('по умолчанию запрашивает фонды (includeFunds не отправляется) и показывает переключатели', async () => {
    renderAnalytics();

    expect(await screen.findByTestId('composition-funds-toggle')).toBeInTheDocument();
    expect(screen.getByTestId('rate-funds-toggle')).toBeInTheDocument();
    expect(urls.length).toBeGreaterThan(0);
    expect(urls.every((u) => !u.searchParams.has('includeFunds'))).toBe(true);
  });

  it('без фондов переключатель не показывается', async () => {
    server.use(
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/rate-scenario', () => HttpResponse.json(baseRateScenario)),
    );
    renderAnalytics();

    await waitFor(() => expect(screen.getByTestId('composition-widget')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('rate-scenario-widget')).toBeInTheDocument());
    expect(screen.queryByTestId('composition-funds-toggle')).not.toBeInTheDocument();
    expect(screen.queryByTestId('rate-funds-toggle')).not.toBeInTheDocument();
  });

  it('«Только облигации» уходит в запрос includeFunds=false и скрывает фонды в сценариях', async () => {
    renderAnalytics();
    const toggle = await screen.findByTestId('rate-funds-toggle');
    expect(await screen.findByTestId('rate-scenario-funds')).toBeInTheDocument();

    await userEvent.click(within(toggle).getByText('Только облигации'));

    await waitFor(() => expect(urls.filter((u) => u.searchParams.get('includeFunds') === 'false')).toHaveLength(2)); // композиция + сценарии
    await waitFor(() => expect(screen.queryByTestId('rate-scenario-funds')).not.toBeInTheDocument());
    // Переключатель остаётся (hasFunds приходит и в облигационном режиме), чтобы вернуться к фондам.
    expect(screen.getByTestId('rate-funds-toggle')).toBeInTheDocument();
  });

  it('?funds=0 в URL сразу запрашивает облигационную картину', async () => {
    renderAnalytics('/analytics?funds=0');

    await waitFor(() => expect(urls.length).toBeGreaterThanOrEqual(2));
    expect(urls.every((u) => u.searchParams.get('includeFunds') === 'false')).toBe(true);
  });

  it('разрез «Класс активов» доступен при фондах и выбирается без пустого состояния', async () => {
    renderAnalytics();
    const widget = await screen.findByTestId('composition-widget');

    await userEvent.click(within(widget).getByText('Класс активов'));

    await waitFor(() => expect(within(widget).getByRole('radio', { name: 'Класс активов' })).toBeChecked());
    expect(screen.queryByTestId('composition-empty')).not.toBeInTheDocument();
  });

  it('разреза «Класс активов» нет, когда фонды не учтены', async () => {
    renderAnalytics('/analytics?funds=0');
    const widget = await screen.findByTestId('composition-widget');
    expect(within(widget).queryByText('Класс активов')).not.toBeInTheDocument();
  });

  it('список фондов в сценариях: источник дюрации подписан как оценка, неизвестная — со ссылкой в карточку фонда', async () => {
    renderAnalytics();

    const tofz = await screen.findByTestId('rate-fund-5');
    expect(tofz).toHaveTextContent('TOFZ');
    expect(tofz).toHaveTextContent('4.20 г.');
    expect(tofz).toHaveTextContent('оценка по индексу RGBI');
    expect(tofz).toHaveTextContent(/-3\s990/); // вклад при +100 б.п.

    const tpay = screen.getByTestId('rate-fund-7');
    expect(tpay).toHaveTextContent('неизвестна');
    expect(screen.getByTestId('rate-fund-link-7')).toHaveAttribute('href', '/funds/7');
  });
});

describe('buildScatterChartData', () => {
  it('plots points by Macaulay duration, not modified duration', () => {
    const { points } = buildScatterChartData(baseScatter);

    const minfin = points.find((p) => p.issuer === 'Минфин РФ')!;
    // macaulayDuration = 3.4 в фикстуре, modifiedDuration = 3.2 — ось X должна брать Маколея,
    // чтобы положение точки относительно кривой совпадало со знаком G-спреда (он на Маколее).
    expect(minfin.durationYears).toBe(3.4);
    expect(minfin.durationYears).not.toBe(minfin.modifiedDuration);
  });

  it('keeps the risk-free curve on its own term axis', () => {
    const { curve } = buildScatterChartData(baseScatter);

    expect(curve.map((c) => c.durationYears)).toContain(5);
  });
});
