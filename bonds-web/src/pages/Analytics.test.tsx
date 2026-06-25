import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Analytics } from './Analytics';
import { useAnalyticsStore } from '../store/useAnalyticsStore';
import { useAuthStore } from '../store/useAuthStore';
import type { CompositionResponse, ScatterResponse, XirrResponse } from '../api/types';

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
      issuer: 'Минфин РФ',
      modifiedDuration: 3.2,
      effectiveYield: 12.5,
      yieldKind: 'Ytm',
      isFloater: false,
      isIndexed: false,
      isEstimated: false,
      dataIncomplete: false,
    },
    {
      positionId: 2,
      instrumentId: 11,
      issuer: 'РЖД',
      modifiedDuration: 1.5,
      effectiveYield: 9.4,
      yieldKind: 'Current',
      isFloater: true,
      isIndexed: false,
      isEstimated: true,
      dataIncomplete: false,
    },
  ],
  curve: [
    { termYears: 0.25, yield: 10 },
    { termYears: 5, yield: 11 },
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

const baseXirr: XirrResponse = {
  currentXirr: 13.2,
  history: [
    { date: '2026-06-01', marketValueRub: 200000, investedRub: 180000, xirr: 12.8 },
    { date: '2026-06-25', marketValueRub: 205000, investedRub: 180000, xirr: 13.2 },
  ],
  disclaimer: '',
};

describe('Analytics', () => {
  beforeEach(() => {
    useAnalyticsStore.setState({
      scatter: null,
      composition: null,
      xirr: null,
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
});
