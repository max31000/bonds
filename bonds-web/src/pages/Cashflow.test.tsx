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

    await waitFor(() => expect(screen.getByText('есть оценочные потоки')).toBeInTheDocument());
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
    expect(screen.queryByTestId('cashflow-by-position-table')).not.toBeInTheDocument();

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('toggle-by-position'));

    await waitFor(() =>
      expect(screen.getByTestId('cashflow-by-position-table')).toBeInTheDocument(),
    );
  });

  it('shows an error state without crashing when the request fails', async () => {
    server.use(http.get('*/api/cashflow', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderCashflow();

    await waitFor(() => expect(screen.getByTestId('cashflow-error')).toBeInTheDocument());
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
