import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
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

describe('Dashboard — деньги на счетах (задача 61)', () => {
  beforeEach(() => {
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
    useCredentialsStore.setState({ capabilities: null });
  });

  it('рисует блок «Деньги на счетах» со свободными рублями и пополнением', async () => {
    const date = new Date();
    date.setUTCDate(date.getUTCDate() - 2);
    server.use(
      http.get('*/api/cash', () =>
        HttpResponse.json({
          brokers: [
            {
              broker: 'tinvest',
              balances: [{ currency: 'RUB', amount: 42000, amountRub: 42000 }],
              cashRubOnly: 42000,
              totalRub: 42000,
              syncedAt: null,
              dataIncomplete: false,
              recentMovements: [{ date: date.toISOString(), type: 'deposit', amount: 30000, currency: 'RUB', amountRub: 30000 }],
            },
          ],
          disclaimer: '',
        }),
      ),
    );
    renderDashboard();

    await waitFor(() => expect(screen.getByTestId('cash-rub-only-tinvest')).toHaveTextContent(/42\s000\s₽/));
    expect(screen.getByTestId('widget-cash')).toBeInTheDocument();
    expect(screen.getAllByTestId('cash-movement-row')).toHaveLength(1);
  });

  it('отказ /api/cash не роняет остальной «Обзор»', async () => {
    server.use(http.get('*/api/cash', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderDashboard();

    expect(await screen.findByTestId('widget-cash-error')).toBeInTheDocument();
    expect(screen.getByTestId('kpi-total-balance')).toBeInTheDocument();
  });
});
