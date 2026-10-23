import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import userEvent from '@testing-library/user-event';
import { server } from '../test/msw-handlers';
import { baseFreedom, capsFreedomOff, capsFreedomOn, luvUsd, vukeGbp } from '../test/freedomFixtures';
import { Positions } from './Positions';
import { useFreedomStore } from '../store/useFreedomStore';
import { useCredentialsStore } from '../store/useCredentialsStore';
import { useCurrenciesStore } from '../store/useCurrenciesStore';
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

describe('Positions — вкладка «Freedom» (задача 60)', () => {
  beforeEach(() => {
    useFreedomStore.setState({ holdings: null, loaded: false, isLoading: false, error: null });
    useCredentialsStore.setState({ capabilities: null });
    useCurrenciesStore.setState({ currencies: [], totals: null, disclaimer: '', loaded: false, isLoading: false, error: null });
    useFundsStore.setState({ funds: [], totals: null, disclaimer: '', isLoading: false, error: null });
  });

  it('вкладка видна только при capabilities.freedom', async () => {
    useCredentialsStore.setState({ capabilities: capsFreedomOff });
    renderPositions();
    await screen.findByTestId('positions-tab-toggle');
    await waitFor(() => expect(useCurrenciesStore.getState().loaded).toBe(true));
    expect(screen.queryByLabelText('Freedom')).not.toBeInTheDocument();
  });

  it('без capability ?tab=freedom открывает облигации и не запрашивает счёт Freedom', async () => {
    let requested = false;
    server.use(
      http.get('*/api/brokers/freedom/holdings', () => {
        requested = true;
        return HttpResponse.json(baseFreedom);
      }),
    );
    useCredentialsStore.setState({ capabilities: capsFreedomOff });
    renderPositions('/positions?tab=freedom');
    await screen.findByTestId('positions-tab-toggle');
    await waitFor(() => expect(useCurrenciesStore.getState().loaded).toBe(true));
    expect(screen.queryByTestId('freedom-table')).not.toBeInTheDocument();
    expect(requested).toBe(false);
  });

  it('с capability вкладка видна, переключение показывает таблицу', async () => {
    server.use(http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json(baseFreedom)));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderPositions();

    await userEvent.click(await screen.findByLabelText('Freedom'));

    expect(await screen.findByTestId('freedom-table')).toBeInTheDocument();
  });

  it('таблица рендерит позиции в USD, EUR и GBP с кодом валюты и рублями', async () => {
    server.use(http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json(baseFreedom)));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderPositions('/positions?tab=freedom');

    const usd = await screen.findByTestId('freedom-row-LUV.US');
    expect(usd).toHaveTextContent('Southwest Airlines');
    expect(usd).toHaveTextContent('13');
    expect(usd).toHaveTextContent(/540,28\s*USD/); // стоимость в валюте бумаги, с кодом, не со знаком ₽
    expect(usd).toHaveTextContent(/41,56\s*USD/);
    expect(usd).toHaveTextContent(/40,30\s*USD/);
    expect(usd).toHaveTextContent('3.13%'); // ДОЛЯ 0.0313 → проценты ровно один раз
    expect(usd).toHaveTextContent(/43\s222\s₽/);

    const eur = screen.getByTestId('freedom-row-ROBO.EU');
    expect(eur).toHaveTextContent(/81,21\s*EUR/);
    expect(eur).toHaveTextContent(/24,4075\s*EUR/); // цена ETF — 4 знака
    expect(eur).toHaveTextContent('ETF');
    expect(eur).toHaveTextContent(/7\s309\s₽/);

    const gbp = screen.getByTestId('freedom-row-VUKE.UK');
    expect(gbp).toHaveTextContent(/72,00\s*GBP/);
    expect(gbp).toHaveTextContent(/7\s920\s₽/);

    // В ячейках сумм в валюте бумаги знака рубля нет — он только в колонке «Стоимость, ₽».
    const cells = within(usd).getAllByRole('cell');
    expect(cells[5]).not.toHaveTextContent('₽');
  });

  it('деньги по валютам и итог в рублях и долларах', async () => {
    server.use(http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json(baseFreedom)));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderPositions('/positions?tab=freedom');

    expect(await screen.findByTestId('freedom-cash-USD')).toHaveTextContent(/4,77\s*USD/);
    expect(screen.getByTestId('freedom-cash-EUR')).toHaveTextContent(/88,57\s*EUR/);
    const totals = screen.getByTestId('freedom-totals');
    expect(totals).toHaveTextContent(/66\s804\s₽/);
    expect(totals).toHaveTextContent(/835\s*USD/);
    expect(screen.queryByTestId('freedom-incomplete')).not.toBeInTheDocument();
  });

  it('при dataIncomplete есть пометка, а позиция без курса показывает «нет курса»', async () => {
    const incomplete = {
      ...baseFreedom,
      positions: [luvUsd, { ...vukeGbp, marketValueRub: null }],
      dataIncomplete: true,
    };
    server.use(http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json(incomplete)));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderPositions('/positions?tab=freedom');

    expect(await screen.findByTestId('freedom-incomplete')).toHaveTextContent('Нет курса валюты');
    expect(within(screen.getByTestId('freedom-row-VUKE.UK')).getByTestId('freedom-rub-missing')).toBeInTheDocument();
    // Сумма в валюте бумаги при этом остаётся.
    expect(screen.getByTestId('freedom-row-VUKE.UK')).toHaveTextContent(/72,00\s*GBP/);
  });

  it('пустой счёт — EmptyState', async () => {
    server.use(http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json({ ...baseFreedom, positions: [], cash: [] })));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderPositions('/positions?tab=freedom');

    expect(await screen.findByTestId('freedom-empty')).toBeInTheDocument();
  });

  it('отказ эндпоинта — ErrorState, вкладка остаётся', async () => {
    server.use(http.get('*/api/brokers/freedom/holdings', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderPositions('/positions?tab=freedom');

    expect(await screen.findByTestId('freedom-error')).toBeInTheDocument();
    expect(screen.getByLabelText('Freedom')).toBeInTheDocument();
  });
});
