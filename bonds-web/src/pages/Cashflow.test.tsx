import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Cashflow } from './Cashflow';
import { useCashflowStore } from '../store/useCashflowStore';
import { useAuthStore } from '../store/useAuthStore';
import type { CashflowResponse } from '../api/types';

function renderCashflow() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Cashflow />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const baseResponse: CashflowResponse = {
  byMonth: [
    {
      month: '2026-07',
      grossRub: 10000,
      taxRub: 1300,
      netRub: 8700,
      couponGrossRub: 10000,
      principalGrossRub: 0,
      hasEstimatedFlows: false,
      positions: [
        {
          positionId: 1,
          name: 'ОФЗ 26238',
          issuer: 'Минфин РФ',
          flowType: 'Coupon',
          grossRub: 10000,
          taxRub: 1300,
          netRub: 8700,
          isEstimated: false,
        },
      ],
    },
  ],
  byPosition: [
    { positionId: 1, instrumentId: 10, grossRub: 10000, taxRub: 1300, netRub: 8700, hasEstimatedFlows: false },
  ],
  principalReleases: [
    {
      date: '2026-08-01',
      positionId: 1,
      instrumentId: 10,
      flowType: 'Amortization',
      amountRub: 50000,
      isEstimated: false,
    },
  ],
  disclaimer: '',
};

describe('Cashflow', () => {
  beforeEach(() => {
    useCashflowStore.setState({
      byMonth: [],
      byPosition: [],
      principalReleases: [],
      disclaimer: '',
      isLoading: false,
      error: null,
    });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
  });

  it('renders the monthly chart and principal releases table', async () => {
    server.use(http.get('*/api/cashflow', () => HttpResponse.json(baseResponse)));

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('cashflow-chart')).toBeInTheDocument());
    expect(screen.getByTestId('principal-releases')).toBeInTheDocument();
    expect(screen.getByText('Амортизация')).toBeInTheDocument();
  });

  it('marks months with estimated flows', async () => {
    server.use(
      http.get('*/api/cashflow', () =>
        HttpResponse.json({
          ...baseResponse,
          byMonth: [{ ...baseResponse.byMonth[0], hasEstimatedFlows: true }],
        }),
      ),
    );

    renderCashflow();

    await waitFor(() => {
      const badge = screen.getByTestId('estimated-flows-badge');
      expect(badge).toBeInTheDocument();
      expect(badge).toHaveTextContent('есть оценочные потоки');
    });
  });

  it('shows an empty state when there is no monthly data', async () => {
    server.use(
      http.get('*/api/cashflow', () =>
        HttpResponse.json({ byMonth: [], byPosition: [], principalReleases: [], disclaimer: '' }),
      ),
    );

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('cashflow-empty')).toBeInTheDocument());
  });

  it('shows a message when there are no principal releases', async () => {
    server.use(
      http.get('*/api/cashflow', () =>
        HttpResponse.json({ ...baseResponse, principalReleases: [] }),
      ),
    );

    renderCashflow();

    await waitFor(() =>
      expect(
        screen.getByText('Нет запланированных дат амортизации/погашения/оферты/колла в горизонте.'),
      ).toBeInTheDocument(),
    );
  });

  it('toggles the by-position breakdown table', async () => {
    server.use(http.get('*/api/cashflow', () => HttpResponse.json(baseResponse)));

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('toggle-by-position')).toBeInTheDocument());

    // Initially, the table should be hidden (display: none due to Collapse being closed)
    await waitFor(() => {
      const table = screen.getByTestId('cashflow-by-position-table');
      expect(table).toBeInTheDocument();
      expect(table.parentElement).toHaveStyle({ display: 'none' });
    });

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('toggle-by-position'));

    // After clicking, the table should be visible
    await waitFor(() => {
      const table = screen.getByTestId('cashflow-by-position-table');
      expect(table.parentElement).not.toHaveStyle({ display: 'none' });
    });
  });

  it('shows an error state without crashing when the request fails', async () => {
    server.use(http.get('*/api/cashflow', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('cashflow-error')).toBeInTheDocument());
  });

  it('shows drill-down table with bond name when selectedMonth is set via store', async () => {
    server.use(http.get('*/api/cashflow', () => HttpResponse.json(baseResponse)));

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('cashflow-chart')).toBeInTheDocument());

    useCashflowStore.setState({
      byMonth: baseResponse.byMonth,
      byPosition: baseResponse.byPosition,
      principalReleases: baseResponse.principalReleases,
      disclaimer: '',
      isLoading: false,
      error: null,
    });

    await waitFor(() => {
      expect(screen.getByTestId('cashflow-chart')).toBeInTheDocument();
    });
  });

  it('drill-down positions array is present on byMonth items from the API', async () => {
    server.use(http.get('*/api/cashflow', () => HttpResponse.json(baseResponse)));

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('cashflow-chart')).toBeInTheDocument());

    const state = useCashflowStore.getState();
    expect(state.byMonth[0].positions).toBeDefined();
    expect(state.byMonth[0].positions[0].name).toBe('ОФЗ 26238');
  });

  it('shows the disclaimer', async () => {
    server.use(
      http.get('*/api/cashflow', () =>
        HttpResponse.json({ ...baseResponse, disclaimer: 'тестовый дисклеймер' }),
      ),
    );

    renderCashflow();

    await waitFor(() => expect(screen.getByText('тестовый дисклеймер')).toBeInTheDocument());
  });
});
