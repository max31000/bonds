import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Analytics } from './Analytics';
import { useAnalyticsStore } from '../store/useAnalyticsStore';
import { useAuthStore } from '../store/useAuthStore';
import type { CompositionResponse, RateScenarioResponse, ScatterResponse, TrajectoryResponse, XirrResponse } from '../api/types';

function renderAnalytics() {
  return render(
    <MantineProvider>
      <MemoryRouter>
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
      effectiveYield: 0.125,
      yieldKind: 'Ytm',
      isFloater: false,
      isIndexed: false,
      isEstimated: false,
      dataIncomplete: false,
    },
    {
      positionId: 2,
      instrumentId: 11,
      name: null,
      issuer: 'РЖД',
      modifiedDuration: 1.5,
      effectiveYield: 0.094,
      yieldKind: 'Current',
      isFloater: true,
      isIndexed: false,
      isEstimated: true,
      dataIncomplete: false,
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
    });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
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
