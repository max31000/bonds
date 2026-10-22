import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import userEvent from '@testing-library/user-event';
import { server } from '../test/msw-handlers';
import { baseCurrencies, baseCurrency, soldCurrency } from '../test/currenciesFixtures';
import { Positions } from './Positions';
import { useCurrenciesStore } from '../store/useCurrenciesStore';
import { useFundsStore } from '../store/useFundsStore';

function Loc() {
  const l = useLocation();
  return <div data-testid="loc">{l.pathname + l.search}</div>;
}

function renderPositions(initialEntry = '/positions') {
  return render(
    <MantineProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Loc />
        <Routes>
          <Route path="positions" element={<Positions />} />
          <Route path="income" element={<div data-testid="income-stub" />} />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('Positions — вкладка «Валюта» (задача 56)', () => {
  beforeEach(() => {
    useCurrenciesStore.setState({ currencies: [], totals: null, disclaimer: '', loaded: false, isLoading: false, error: null });
    useFundsStore.setState({ funds: [], totals: null, disclaimer: '', isLoading: false, error: null });
  });

  it('без валюты вкладка «Валюта» скрыта', async () => {
    renderPositions();
    await screen.findByTestId('positions-tab-toggle');
    await waitFor(() => expect(useCurrenciesStore.getState().loaded).toBe(true));
    expect(screen.queryByLabelText('Валюта')).not.toBeInTheDocument();
  });

  it('с валютой вкладка видна; ?tab=currency показывает строки, итоги и дисклеймер про курсовую разницу и налог', async () => {
    server.use(http.get('*/api/currencies', () => HttpResponse.json(baseCurrencies)));
    renderPositions('/positions?tab=currency');

    const row = await screen.findByTestId('currency-row-3');
    expect(row).toHaveTextContent('Китайский юань');
    expect(row).toHaveTextContent('CNY');
    expect(row).toHaveTextContent('1 000'); // единицы валюты, не лоты
    expect(row).toHaveTextContent('11,00'); // средний курс
    expect(row).toHaveTextContent('11,50'); // текущий курс
    expect(row).toHaveTextContent('4.54%'); // курсовая разница, %
    expect(row).toHaveTextContent('2.00%'); // 1м: доля 0.02
    expect(row).toHaveTextContent('-1.50%'); // 3м
    expect(row).toHaveTextContent('—'); // 12м: нет истории
    expect(within(row).getByTestId('rate-sparkline')).toBeInTheDocument();

    const totals = screen.getByTestId('currencies-totals-row');
    expect(totals).toHaveTextContent(/11\s500/);
    expect(totals).toHaveTextContent(/620/);
    expect(screen.getByTestId('currencies-fx-note')).toHaveTextContent('Налог с курсовой разницы');
    expect(screen.getByLabelText('Валюта')).toBeInTheDocument();
  });

  it('проданная валюта (qty 0) остаётся в таблице с пометкой «продана»', async () => {
    server.use(http.get('*/api/currencies', () => HttpResponse.json({ ...baseCurrencies, currencies: [baseCurrency, soldCurrency] })));
    renderPositions('/positions?tab=currency');
    const sold = await screen.findByTestId('currency-row-4');
    expect(sold).toHaveTextContent('продана');
    expect(sold).toHaveTextContent('-35');
  });

  it('только проданная валюта — вкладка всё равно видна', async () => {
    server.use(http.get('*/api/currencies', () => HttpResponse.json({ ...baseCurrencies, currencies: [soldCurrency] })));
    renderPositions();
    expect(await screen.findByLabelText('Валюта')).toBeInTheDocument();
  });

  it('переключатель ведёт на вкладку и пишет ?tab=currency в URL', async () => {
    server.use(http.get('*/api/currencies', () => HttpResponse.json(baseCurrencies)));
    renderPositions();
    await userEvent.click(await screen.findByLabelText('Валюта'));
    expect(await screen.findByTestId('currency-row-3')).toBeInTheDocument();
    expect(screen.getByTestId('loc')).toHaveTextContent('tab=currency');
  });

  it('клик и Enter по строке ведут в «Доходы» с фокусом на валюте', async () => {
    server.use(http.get('*/api/currencies', () => HttpResponse.json(baseCurrencies)));
    renderPositions('/positions?tab=currency');
    const row = await screen.findByTestId('currency-row-3');
    expect(row).toHaveAttribute('tabindex', '0');
    row.focus();
    await userEvent.keyboard('{Enter}');
    await screen.findByTestId('income-stub');
    expect(screen.getByTestId('loc')).toHaveTextContent('/income?asset=currency&focus=3');
  });

  it('?tab=currency без валюты — понятное пустое состояние', async () => {
    renderPositions('/positions?tab=currency');
    expect(await screen.findByTestId('currencies-empty')).toHaveTextContent('Валюты нет');
  });

  it('ошибка /api/currencies показывает ErrorState на вкладке, не падая', async () => {
    server.use(http.get('*/api/currencies', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderPositions('/positions?tab=currency');
    expect(await screen.findByTestId('currencies-error')).toBeInTheDocument();
  });
});
