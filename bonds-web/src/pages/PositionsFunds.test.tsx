import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import userEvent from '@testing-library/user-event';
import { server } from '../test/msw-handlers';
import { baseFund, baseFunds } from '../test/fundsFixtures';
import { Positions } from './Positions';
import { useFundsStore } from '../store/useFundsStore';

function renderPositions(initialEntry = '/positions') {
  return render(
    <MantineProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Positions />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('Positions — вкладка «Фонды» (задача 53)', () => {
  beforeEach(() => {
    useFundsStore.setState({ funds: [], totals: null, disclaimer: '', isLoading: false, error: null });
  });

  it('по умолчанию открыта вкладка облигаций, фонды не запрашиваются', async () => {
    let fundsRequested = false;
    server.use(
      http.get('*/api/funds', () => {
        fundsRequested = true;
        return HttpResponse.json(baseFunds);
      }),
    );
    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-tab-toggle')).toBeInTheDocument());
    expect(screen.queryByTestId('funds-table')).not.toBeInTheDocument();
    expect(fundsRequested).toBe(false);
  });

  it('?tab=funds открывает вкладку: строки фонда, оценка с методом и строка итогов', async () => {
    server.use(http.get('*/api/funds', () => HttpResponse.json(baseFunds)));
    renderPositions('/positions?tab=funds');

    const row = await screen.findByTestId('fund-row-7');
    expect(row).toHaveTextContent('Пассивный доход');
    expect(row).toHaveTextContent('TPAY');
    expect(row).toHaveTextContent('101,35'); // цена пая с копейками
    expect(row).toHaveTextContent('1.35%'); // P&L %
    expect(row).toHaveTextContent('14.25%'); // 12м факт
    expect(within(screen.getByTestId('fund-outlook-7')).getByText('14.88%')).toBeInTheDocument();
    expect(within(screen.getByTestId('fund-outlook-7')).getByText('По выплатам')).toBeInTheDocument();
    expect(row).toHaveTextContent('40.00%'); // доля портфеля

    const totals = screen.getByTestId('funds-totals-row');
    expect(totals).toHaveTextContent('10');
    expect(totals).toHaveTextContent('135');
    expect(screen.getByTestId('funds-key-rate-note')).toHaveTextContent('ключевой ставкой');
    expect(screen.getByTestId('disclaimer')).toHaveTextContent('не рекомендация');
  });

  it('переключатель вкладок переводит между облигациями и фондами', async () => {
    server.use(http.get('*/api/funds', () => HttpResponse.json(baseFunds)));
    renderPositions();

    await userEvent.click(await screen.findByLabelText('Фонды'));
    expect(await screen.findByTestId('fund-row-7')).toBeInTheDocument();

    await userEvent.click(screen.getByLabelText('Облигации'));
    await waitFor(() => expect(screen.queryByTestId('funds-table')).not.toBeInTheDocument());
  });

  it('пустой список — EmptyState с подсказкой про синк', async () => {
    renderPositions('/positions?tab=funds');
    const empty = await screen.findByTestId('funds-empty');
    expect(empty).toHaveTextContent('Фондов нет — они появятся после синка');
  });

  it('ошибка /api/funds показывает ErrorState, не падая', async () => {
    server.use(http.get('*/api/funds', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderPositions('/positions?tab=funds');
    expect(await screen.findByTestId('funds-error')).toBeInTheDocument();
  });

  it('оценка недоступна — прочерк без бейджа метода', async () => {
    const unavailable = {
      ...baseFund,
      outlook: { ...baseFund.outlook, expectedAnnualYieldFraction: null, afterTaxYieldFraction: null, expectedMonthlyIncomeRub: null, unavailableReason: 'мало данных' },
    };
    server.use(http.get('*/api/funds', () => HttpResponse.json({ ...baseFunds, funds: [unavailable] })));
    renderPositions('/positions?tab=funds');

    const cell = await screen.findByTestId('fund-outlook-7');
    expect(cell).toHaveTextContent('—');
    expect(within(cell).queryByText('По выплатам')).not.toBeInTheDocument();
  });
});
