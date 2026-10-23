import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { CashOverviewSection } from './CashOverviewSection';
import type { CashOverviewResponse } from '../api/types';

function renderSection() {
  return render(
    <MantineProvider>
      <CashOverviewSection />
    </MantineProvider>,
  );
}

// Даты относительно «сегодня» (никаких абсолютных дат).
function daysAgoIso(days: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString();
}

const overview: CashOverviewResponse = {
  brokers: [
    {
      broker: 'tinvest',
      balances: [
        { currency: 'RUB', amount: 109000, amountRub: 109000 },
        { currency: 'T01', amount: 1000, amountRub: 11000 },
      ],
      cashRubOnly: 109000,
      totalRub: 120000,
      syncedAt: daysAgoIso(0),
      dataIncomplete: false,
      recentMovements: [
        { date: daysAgoIso(3), type: 'deposit', amount: 50000, currency: 'RUB', amountRub: 50000 },
        { date: daysAgoIso(9), type: 'withdrawal', amount: -12000, currency: 'RUB', amountRub: -12000 },
      ],
    },
    {
      broker: 'freedom',
      balances: [
        { currency: 'USD', amount: 10, amountRub: null },
        { currency: 'EUR', amount: 20, amountRub: 1800 },
      ],
      cashRubOnly: null,
      totalRub: 1800,
      syncedAt: daysAgoIso(0),
      dataIncomplete: true,
      recentMovements: [{ date: daysAgoIso(1), type: 'deposit', amount: 100, currency: 'USD', amountRub: 8000 }],
    },
  ],
  disclaimer: '',
};

describe('CashOverviewSection (задача 61)', () => {
  it('показывает деньги по брокерам и валютам; свободные рубли — отдельно от валюты', async () => {
    server.use(http.get('*/api/cash', () => HttpResponse.json(overview)));
    renderSection();

    const tinvest = await screen.findByTestId('cash-broker-tinvest');
    expect(within(tinvest).getByTestId('cash-rub-only-tinvest')).toHaveTextContent(/109\s000\s₽/);
    expect(within(tinvest).getByText('T01')).toBeInTheDocument();
    expect(within(tinvest).getByText(/11\s000\s₽/)).toBeInTheDocument();
    expect(screen.queryByTestId('cash-incomplete-tinvest')).not.toBeInTheDocument();

    const freedom = screen.getByTestId('cash-broker-freedom');
    expect(within(freedom).getByText('USD')).toBeInTheDocument();
    expect(within(freedom).queryByTestId('cash-rub-only-freedom')).not.toBeInTheDocument();
    expect(within(freedom).getByText('—')).toBeInTheDocument();
    expect(screen.getByTestId('cash-incomplete-freedom')).toBeInTheDocument();
  });

  it('последние пополнения и выводы: новые сверху, тип и знак брокера, не-рубль с оценкой в рублях', async () => {
    server.use(http.get('*/api/cash', () => HttpResponse.json(overview)));
    renderSection();

    await screen.findByTestId('cash-movements');
    const rows = screen.getAllByTestId('cash-movement-row');
    expect(rows).toHaveLength(3);
    // самый новый — пополнение Freedom в USD (1 день назад), затем T-Invest 3 и 9 дней назад
    expect(rows[0]).toHaveTextContent('Freedom');
    expect(rows[0]).toHaveTextContent('Пополнение');
    expect(rows[0]).toHaveTextContent(/100,00\s*USD/);
    expect(rows[0]).toHaveTextContent(/≈\s*8\s000\s₽/);
    expect(rows[1]).toHaveTextContent('T-Invest');
    expect(rows[1]).toHaveTextContent(/50\s000,00/);
    expect(rows[2]).toHaveTextContent('Вывод');
    expect(rows[2]).toHaveTextContent(/-12\s000,00|−12\s000,00/);
  });

  it('нет счетов — пустое состояние', async () => {
    renderSection();

    expect(await screen.findByTestId('widget-cash-empty')).toBeInTheDocument();
    expect(screen.queryByTestId('cash-movements')).not.toBeInTheDocument();
  });

  it('счёт есть, движений нет — пометка вместо таблицы', async () => {
    server.use(
      http.get('*/api/cash', () =>
        HttpResponse.json({ ...overview, brokers: [{ ...overview.brokers[0], recentMovements: [] }] }),
      ),
    );
    renderSection();

    expect(await screen.findByTestId('cash-movements-empty')).toBeInTheDocument();
    expect(screen.queryAllByTestId('cash-movement-row')).toHaveLength(0);
  });

  it('отказ эндпоинта — ошибка только этого блока', async () => {
    server.use(http.get('*/api/cash', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderSection();

    await waitFor(() => expect(screen.getByTestId('widget-cash-error')).toBeInTheDocument());
    expect(screen.queryByTestId('cash-movements')).not.toBeInTheDocument();
  });
});
