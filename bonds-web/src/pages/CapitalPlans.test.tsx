import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { baseBaskets, rubPortfolio, temporaryBasket } from '../test/basketsFixtures';
import { capsFreedomOff, capsFreedomOn } from '../test/freedomFixtures';
import { inflowPlan, withdrawPlan } from '../test/plansFixtures';
import { Capital } from './Capital';
import { useBasketsStore } from '../store/useBasketsStore';
import { useCredentialsStore } from '../store/useCredentialsStore';
import { usePlansStore } from '../store/usePlansStore';
import type { BasketsResponse } from '../api/types';

function renderCapital() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Capital />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const withTemporaryMembers: BasketsResponse = {
  ...baseBaskets,
  portfolios: [
    {
      ...rubPortfolio,
      baskets: rubPortfolio.baskets.map((b) =>
        b.code === 'temporary'
          ? { ...temporaryBasket, members: [{ id: 41, broker: 'tinvest', instrumentKey: 'TPAY', subWeight: null, matchRule: 'instrument' as const }] }
          : b,
      ),
    },
    baseBaskets.portfolios[1],
  ],
};

describe('Capital — кнопки планов (задача 63, режим наблюдения)', () => {
  beforeEach(() => {
    useBasketsStore.setState({ data: null, isLoading: false, error: null });
    usePlansStore.setState({ data: null, isLoading: false, error: null, decidingId: null });
    useCredentialsStore.setState({ capabilities: capsFreedomOff });
    server.use(http.get('*/api/baskets', () => HttpResponse.json(withTemporaryMembers)));
  });

  it('показывает три кнопки и напоминает, что сделки не отправляются', async () => {
    renderCapital();

    expect(await screen.findByTestId('plan-action-inflow')).toHaveTextContent('Распределить сумму…');
    expect(screen.getByTestId('plan-action-withdraw')).toHaveTextContent('Вывести…');
    expect(screen.getByTestId('plan-action-exit')).toHaveTextContent('Выйти из временной корзины');
    expect(screen.getByTestId('capital-plan-actions')).toHaveTextContent('сделки не отправляются');
  });

  it('«Распределить сумму…»: сумма уходит в POST /api/plans/inflow, результат — ссылка на «Планы»', async () => {
    let body: unknown = null;
    server.use(
      http.post('*/api/plans/inflow', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ plan: inflowPlan, notes: [], dryRun: true, disclaimer: '' });
      }),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('plan-action-inflow'));
    await userEvent.type(await screen.findByTestId('plan-action-amount'), '5000');
    await userEvent.click(screen.getByTestId('plan-action-submit'));

    expect(await screen.findByTestId('plan-action-created')).toHaveTextContent('сделки не отправляются');
    expect(body).toEqual({ amount: 5000, portfolio: 'rub' });
    expect(screen.getByTestId('plan-action-open-plans')).toHaveAttribute('href', '/plans');
  });

  it('«Вывести…» в %: проценты уходят ДОЛЕЙ (10 → 0.1), результат показывает «к выводу после налога и комиссии»', async () => {
    let body: Record<string, unknown> = {};
    server.use(
      http.post('*/api/plans/withdraw', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ plan: withdrawPlan, notes: [], dryRun: true, disclaimer: '' });
      }),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('plan-action-withdraw'));
    await userEvent.click(await screen.findByText('% портфеля'));
    await userEvent.type(screen.getByTestId('plan-action-amount'), '10');
    await userEvent.click(screen.getByTestId('plan-action-submit'));

    expect(await screen.findByTestId('plan-action-created')).toHaveTextContent(/2\s131,02\sRUB/);
    expect(body).toEqual({ fraction: 0.1, portfolio: 'rub' });
  });

  it('«Вывести…» с разрешением продавать временную корзину шлёт includeBaskets', async () => {
    let body: Record<string, unknown> = {};
    server.use(
      http.post('*/api/plans/withdraw', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ plan: withdrawPlan, notes: [], dryRun: true, disclaimer: '' });
      }),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('plan-action-withdraw'));
    await userEvent.type(await screen.findByTestId('plan-action-amount'), '2000');
    await userEvent.click(screen.getByLabelText('Временные'));
    await userEvent.click(screen.getByTestId('plan-action-submit'));

    await screen.findByTestId('plan-action-created');
    expect(body).toEqual({ amount: 2000, portfolio: 'rub', includeBaskets: ['temporary'] });
  });

  it('«Выйти из временной корзины» шлёт код корзины; «плана нет» показывает причины', async () => {
    let body: unknown = null;
    server.use(
      http.post('*/api/plans/exit-basket', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ plan: null, notes: ['TPAY: нет стакана (bid/ask)'], dryRun: true, disclaimer: '' });
      }),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('plan-action-exit'));
    await userEvent.click(await screen.findByTestId('plan-action-submit'));

    expect(await screen.findByTestId('plan-action-none')).toHaveTextContent('нет стакана');
    expect(body).toEqual({ code: 'temporary' });
  });

  it('ошибка бэкенда (422) показывается в диалоге', async () => {
    server.use(http.post('*/api/plans/inflow', () => HttpResponse.json({ error: 'amount должен быть положительным.' }, { status: 422 })));
    renderCapital();

    await userEvent.click(await screen.findByTestId('plan-action-inflow'));
    await userEvent.type(await screen.findByTestId('plan-action-amount'), '1');
    await userEvent.click(screen.getByTestId('plan-action-submit'));

    expect(await screen.findByTestId('plan-action-error')).toHaveTextContent('amount должен быть положительным');
  });

  it('выбор портфеля виден только с Freedom', async () => {
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    server.use(http.get('*/api/baskets', () => HttpResponse.json(baseBaskets)));
    renderCapital();

    await userEvent.click(await screen.findByTestId('plan-action-inflow'));
    await waitFor(() => expect(screen.getByTestId('plan-action-portfolio')).toBeInTheDocument());
  });

  it('без брокерских счетов блока планов нет', async () => {
    server.use(
      http.get('*/api/baskets', () =>
        HttpResponse.json({ ...baseBaskets, portfolios: baseBaskets.portfolios.map((p) => ({ ...p, hasAccount: false })) }),
      ),
    );
    renderCapital();

    await screen.findByTestId('capital-portfolio-rub');
    expect(screen.queryByTestId('capital-plan-actions')).not.toBeInTheDocument();
  });
});
