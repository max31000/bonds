import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { baseFunds, baseSummary } from '../test/fundsFixtures';
import { Dashboard } from './Dashboard';
import { useLiveStore } from '../store/useLiveStore';

function renderDashboard() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('Dashboard — общий баланс с фондами (задача 53)', () => {
  beforeEach(() => {
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
  });

  it('«Стоимость портфеля» = облигации + фонды + деньги, XIRR облигаций и портфеля, KPI «Фонды»', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json(baseSummary)),
      http.get('*/api/funds', () => HttpResponse.json(baseFunds)),
      http.get('*/api/analytics/xirr', () => HttpResponse.json({ currentXirr: 0.132, history: [], disclaimer: '' })),
    );
    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('kpi-total-balance')).toHaveTextContent('123'));
    const total = screen.getByTestId('kpi-total-balance');
    expect(total).toHaveTextContent('облигации');
    expect(total).toHaveTextContent('фонды');
    expect(screen.queryByTestId('kpi-total-balance-mismatch')).not.toBeInTheDocument();

    await waitFor(() => expect(screen.getByTestId('kpi-funds')).toHaveTextContent('10'));
    expect(screen.getByTestId('kpi-xirr')).toHaveTextContent('XIRR облигаций');
    await waitFor(() => expect(screen.getByTestId('kpi-xirr')).toHaveTextContent('XIRR портфеля (с фондами): 12.50%'));
  });

  it('фонд без цены: предупреждение в KPI «Фонды» и «Стоимость портфеля»', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json({ ...baseSummary, fundsDataIncomplete: true })),
      http.get('*/api/funds', () => HttpResponse.json({ ...baseFunds, totals: { ...baseFunds.totals, dataIncomplete: true } })),
    );
    renderDashboard();
    expect(await screen.findByTestId('kpi-total-balance-incomplete')).toBeInTheDocument();
    expect(await screen.findByTestId('kpi-funds-incomplete')).toBeInTheDocument();
  });

  it('расхождение с брокером > 1% — подсказка', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json({ ...baseSummary, brokerTotalRub: 133135, brokerDiffRub: 10000 })),
    );
    renderDashboard();
    const hint = await screen.findByTestId('kpi-total-balance-mismatch');
    expect(hint).toHaveTextContent('расходится с брокером');
    expect(hint).toHaveTextContent('другие активы/валюта');
  });

  it('фондов нет — KPI «Фонды» не показывается', async () => {
    server.use(http.get('*/api/portfolio/summary', () => HttpResponse.json(baseSummary)));
    renderDashboard();
    await waitFor(() => expect(screen.getByTestId('kpi-total-balance')).toHaveTextContent('123'));
    expect(screen.queryByTestId('kpi-funds')).not.toBeInTheDocument();
  });

  it('отказ /api/portfolio/summary не роняет остальные виджеты', async () => {
    server.use(http.get('*/api/portfolio/summary', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderDashboard();
    await waitFor(() => expect(screen.getByTestId('kpi-total-balance-error')).toBeInTheDocument());
    expect(screen.getByTestId('kpi-xirr')).toBeInTheDocument();
    expect(screen.getByTestId('kpi-next-payment')).toBeInTheDocument();
    expect(screen.getByTestId('kpi-cash')).toBeInTheDocument();
  });
});
