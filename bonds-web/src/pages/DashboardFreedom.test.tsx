import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { baseSummary } from '../test/fundsFixtures';
import { baseFreedom, capsFreedomOff, capsFreedomOn } from '../test/freedomFixtures';
import { Dashboard } from './Dashboard';
import { useLiveStore } from '../store/useLiveStore';
import { useCredentialsStore } from '../store/useCredentialsStore';

function renderDashboard() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    </MantineProvider>,
  );
}

// T-Invest 123 135 ₽ + Freedom 66 804 ₽ = капитал 189 939 ₽; totalRub (T-Invest) не меняется.
const withOtherBrokers = { ...baseSummary, otherBrokersValueRub: 66804.2, capitalTotalRub: 123135 + 66804.2 };

describe('Dashboard — Freedom (задача 60)', () => {
  beforeEach(() => {
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
    useCredentialsStore.setState({ capabilities: null });
  });

  it('при capabilities.freedom=false KPI «Freedom» и «Капитал всего» нет, запрос счёта не уходит', async () => {
    let requested = false;
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json(withOtherBrokers)),
      http.get('*/api/brokers/freedom/holdings', () => {
        requested = true;
        return HttpResponse.json(baseFreedom);
      }),
    );
    useCredentialsStore.setState({ capabilities: capsFreedomOff });
    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('kpi-total-balance')).toHaveTextContent(/123\s135\s₽/));
    expect(screen.queryByTestId('kpi-freedom')).not.toBeInTheDocument();
    expect(screen.queryByTestId('kpi-capital')).not.toBeInTheDocument();
    expect(requested).toBe(false);
  });

  it('при capabilities.freedom=true: KPI «Freedom» (₽ и $) и «Капитал всего»', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json(withOtherBrokers)),
      http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json(baseFreedom)),
    );
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderDashboard();

    const freedom = await screen.findByTestId('kpi-freedom');
    await waitFor(() => expect(freedom).toHaveTextContent(/66\s804\s₽/));
    expect(await screen.findByTestId('kpi-freedom-usd')).toHaveTextContent(/835\s*USD/);

    const capital = await screen.findByTestId('kpi-capital');
    await waitFor(() => expect(capital).toHaveTextContent(/189\s939\s₽/));
    // «Стоимость портфеля» (T-Invest) без изменений: чужие брокеры в totalRub не подмешаны.
    expect(screen.getByTestId('kpi-total-balance')).toHaveTextContent(/123\s135\s₽/);
  });

  it('dataIncomplete: у KPI «Freedom» и «Капитал всего» есть пометка про курс', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json({ ...withOtherBrokers, otherBrokersDataIncomplete: true })),
      http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json({ ...baseFreedom, dataIncomplete: true })),
    );
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderDashboard();

    expect(await screen.findByTestId('kpi-freedom-incomplete')).toHaveTextContent('Нет курса валюты');
    expect(await screen.findByTestId('kpi-capital-incomplete')).toHaveTextContent('итог занижен');
  });

  it('отказ счёта Freedom не роняет остальные KPI', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json(withOtherBrokers)),
      http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
    );
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderDashboard();

    expect(await screen.findByTestId('kpi-freedom-error')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId('kpi-capital')).toHaveTextContent(/189\s939\s₽/));
    expect(screen.getByTestId('kpi-total-balance')).toHaveTextContent(/123\s135\s₽/);
  });
});
