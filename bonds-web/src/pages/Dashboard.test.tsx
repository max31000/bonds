import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Dashboard } from './Dashboard';
import { useAuthStore } from '../store/useAuthStore';
import { useLiveStore } from '../store/useLiveStore';
import { useSignalsStore } from '../store/useSignalsStore';
import type { CashflowResponse, CompositionResponse, PositionsResponse, XirrResponse } from '../api/types';

function renderDashboard() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const basePositions: PositionsResponse = {
  positions: [
    {
      positionId: 1,
      instrumentId: 10,
      name: 'ОФЗ 26238',
      isin: 'RU000A1038V6',
      issuer: 'Минфин РФ',
      sector: 'ОФЗ',
      quantity: 100,
      marketValueRub: 105000,
      accruedPerBondRub: 0,
      accruedTotalRub: 0,
      currencyRub: 'RUB',
      couponType: 'Fixed',
      maturityDate: '2030-01-01',
      horizonDate: '2030-01-01',
      calculatedToOffer: false,
      ytmEffective: 0.125,
      currentYield: 0.118,
      modifiedDuration: 3.2,
      gSpread: 0.005,
      isFloater: false,
      isIndexed: false,
      isEstimated: false,
      dataIncomplete: false,
      isOutOfScopeCurrency: false,
      averageCostRub: 980,
      investedRub: 98000,
      unrealizedPnlRub: 7000,
      unrealizedPnlPercent: 0.0714,
      couponsReceivedRub: 3500,
      totalReturnPercent: 0.107,
      costBasisIncomplete: false,
    },
  ],
  disclaimer: '',
};

const baseXirr: XirrResponse = {
  currentXirr: 0.132,
  history: [{ date: '2026-06-25', marketValueRub: 105000, investedRub: 98000, xirr: 0.132 }],
  disclaimer: '',
};

const baseCashflow: CashflowResponse = {
  byMonth: [],
  byPosition: [],
  principalReleases: [],
  nextPayments: [
    { date: '2026-07-15', name: 'ОФЗ 26238', issuer: 'Минфин РФ', flowType: 'Coupon', netRub: 8700, isEstimated: false },
  ],
  disclaimer: '',
};

const baseComposition: CompositionResponse = {
  totalMarketValueRub: 105000,
  byIssuer: [{ key: 'Минфин РФ', marketValueRub: 105000, sharePercent: 100 }],
  bySector: [],
  byCouponType: [],
  byDurationBucket: [],
  disclaimer: '',
};

describe('Dashboard', () => {
  beforeEach(() => {
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
    useSignalsStore.setState({ signals: [], isLoading: false, error: null });

    // useLiveQuotes грубо фильтрует поллинг по торговым часам — фиксируем время вне окна,
    // чтобы тест не зависел от поллинга (дашборд не требует его для рендера KPI).
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-07-03T02:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders the KPI row from MSW data', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('dashboard-kpi-row')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('kpi-portfolio-value')).toHaveTextContent('105'));
    expect(screen.getByTestId('kpi-xirr')).toHaveTextContent('13.20%');
    expect(screen.getByTestId('kpi-next-payment')).toHaveTextContent('8 700');
    expect(screen.getByTestId('kpi-unread-signals')).toBeInTheDocument();
  });

  it('renders the value chart, mini composition, and upcoming cashflow widgets', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('dashboard-value-chart')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('widget-composition')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('widget-upcoming-cashflow')).toBeInTheDocument());
    expect(screen.getAllByText('ОФЗ 26238').length).toBeGreaterThan(0);
  });

  it('keeps other widgets alive when one endpoint returns 500 (independent loading)', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      // XIRR falls over — its KPI should show a graceful fallback, not crash the page.
      http.get('*/api/analytics/xirr', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('kpi-xirr-error')).toBeInTheDocument());
    // The rest of the dashboard still renders normally.
    await waitFor(() => expect(screen.getByTestId('kpi-portfolio-value')).toHaveTextContent('105'));
    expect(screen.getByTestId('kpi-next-payment')).toHaveTextContent('8 700');
    await waitFor(() => expect(screen.getByTestId('widget-composition')).toBeInTheDocument());
  });

  it('keeps the dashboard alive when composition fails but shows its own empty state', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('widget-composition-error')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('kpi-xirr')).toHaveTextContent('13.20%'));
  });

  it('keeps the dashboard alive when cashflow fails', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('kpi-next-payment-error')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('widget-upcoming-cashflow-error')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('kpi-xirr')).toHaveTextContent('13.20%'));
  });

  it('shows a delta on the portfolio value KPI once live data has ticked in', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();
    await waitFor(() => expect(screen.getByTestId('kpi-portfolio-value')).toHaveTextContent('105'));

    useLiveStore.setState({
      positionsById: { 1: { positionId: 1, instrumentId: 10, lastPriceRub: 1100, marketValueRub: 110000, changeDayPercent: 0.0476, isStale: false, asOfUtc: '2026-07-03T10:00:00Z' } },
      totalMarketValueRub: 110000,
      asOfUtc: '2026-07-03T10:00:00Z',
    });

    await waitFor(() => expect(screen.getByTestId('kpi-portfolio-value-delta')).toBeInTheDocument());
  });

  it('opens the explanation popover for the mini composition chart', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('widget-composition-explain-icon')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('widget-composition-explain-icon'), { pointerEventsCheck: 0 });

    await waitFor(() => expect(screen.getByText(/Как стоимость портфеля распределена/)).toBeInTheDocument());
  });

  // ─── T-24: глобальная подсказка про "грязные" цены на KPI «Стоимость портфеля» ─────────────

  it('opens the dirty-price explanation popover for the portfolio value KPI', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json(basePositions)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json(baseXirr)),
      http.get('*/api/cashflow', () => HttpResponse.json(baseCashflow)),
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
    );

    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('kpi-portfolio-value-explain-icon')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('kpi-portfolio-value-explain-icon'), { pointerEventsCheck: 0 });

    await waitFor(() => expect(screen.getByText(/накопленный купонный доход/)).toBeInTheDocument());
  });
});
