import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { baseBaskets, bondsBasket, liquidityBasket, rubPortfolio, usdPortfolio } from '../test/basketsFixtures';
import { capsFreedomOff, capsFreedomOn } from '../test/freedomFixtures';
import { Capital } from './Capital';
import { useBasketsStore } from '../store/useBasketsStore';
import { useCredentialsStore } from '../store/useCredentialsStore';
import type { BasketBatchItem, BasketsResponse } from '../api/types';

function renderCapital() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Capital />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('Capital — страница «Капитал» (задача 62)', () => {
  beforeEach(() => {
    useBasketsStore.setState({ data: null, isLoading: false, error: null });
    useCredentialsStore.setState({ capabilities: capsFreedomOff });
    server.use(http.get('*/api/baskets', () => HttpResponse.json(baseBaskets)));
  });

  it('рендерит рублёвый портфель: корзины, цель, факт, отклонение (в п.п.), состояние, дефицит и дисклеймер', async () => {
    renderCapital();

    expect(await screen.findByTestId('capital-portfolio-rub')).toBeInTheDocument();
    expect(screen.getByTestId('capital-total-rub')).toHaveTextContent(/1\s000\s000\s₽/);

    // Эталон: ликвидность 120 000 при цели 15% → факт 12,0%, отклонение −3,0 п.п., «Ниже цели», не хватает 30 000 ₽.
    expect(screen.getByTestId('basket-target-liquidity')).toHaveTextContent('15,0%');
    expect(screen.getByTestId('basket-weight-liquidity')).toHaveTextContent('12,0%');
    expect(screen.getByTestId('basket-drift-liquidity')).toHaveTextContent('−3,0 п.п.');
    expect(screen.getByTestId('basket-state-liquidity')).toHaveTextContent('Ниже цели');
    expect(screen.getByTestId('basket-deficit-liquidity')).toHaveTextContent(/не хватает 30\s000\s₽/);
    expect(screen.getByTestId('basket-bar-liquidity')).toHaveAttribute('aria-label', expect.stringContaining('цель 15,0%'));

    expect(screen.getByTestId('basket-state-bonds')).toHaveTextContent('Выше цели');
    expect(screen.getByTestId('basket-deficit-bonds')).toHaveTextContent(/избыток 30\s000\s₽/);
    expect(screen.getByTestId('basket-members-liquidity')).toHaveTextContent('TMON@');
    expect(screen.getByTestId('basket-members-bonds')).toHaveTextContent('все бумаги класса «bond»');

    // Корзина без цели — «Вне целей», отклонения нет.
    expect(screen.getByTestId('basket-state-temporary')).toHaveTextContent('Вне целей');
    expect(screen.getByTestId('basket-drift-temporary')).toHaveTextContent('—');

    expect(screen.getByTestId('disclaimer')).toHaveTextContent('не рекомендация');
  });

  it('раздел usd скрыт без capabilities.freedom и виден с ней', async () => {
    const { unmount } = renderCapital();
    await screen.findByTestId('capital-portfolio-rub');
    expect(screen.queryByTestId('capital-portfolio-usd')).not.toBeInTheDocument();
    unmount();

    useBasketsStore.setState({ data: null });
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderCapital();
    expect(await screen.findByTestId('capital-portfolio-usd')).toBeInTheDocument();
    expect(screen.getByTestId('capital-total-usd')).toHaveTextContent(/1\s674\s*USD/);
    expect(screen.getByTestId('basket-state-global_core')).toHaveTextContent('Ниже цели');
  });

  it('база доли ≤ 0: корзина с целью показывает «нет данных», без отклонения и дефицита', async () => {
    const noData: BasketsResponse = {
      ...baseBaskets,
      portfolios: [
        rubPortfolio,
        {
          ...usdPortfolio,
          baskets: usdPortfolio.baskets.map((b) => ({ ...b, value: 0, weight: 0, drift: null, state: 'noData' as const, deficitToTarget: null })),
        },
      ],
    };
    server.use(http.get('*/api/baskets', () => HttpResponse.json(noData)));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderCapital();

    expect(await screen.findByTestId('basket-state-global_core')).toHaveTextContent('Нет данных');
    expect(screen.getByTestId('basket-drift-global_core')).toHaveTextContent('нет данных');
    expect(screen.getByTestId('basket-deficit-global_core')).toHaveTextContent('нет данных');
  });

  it('предупреждает, когда цели портфеля не сходятся к 100% (usd: 60%)', async () => {
    const badUsd: BasketsResponse = {
      ...baseBaskets,
      portfolios: [
        rubPortfolio,
        { ...usdPortfolio, targetsSum: 0.6, baskets: usdPortfolio.baskets.map((b) => ({ ...b, targetWeight: 0.6, target: 0.6 })) },
      ],
    };
    server.use(http.get('*/api/baskets', () => HttpResponse.json(badUsd)));
    useCredentialsStore.setState({ capabilities: capsFreedomOn });
    renderCapital();

    expect(await screen.findByTestId('capital-sum-warning-usd')).toHaveTextContent('60,0%');
    expect(screen.queryByTestId('capital-sum-warning-rub')).not.toBeInTheDocument();
  });

  it('форма: ввод в % уходит в API ДОЛЯМИ (20 → 0.2, 80 → 0.8), ответ обновляет страницу', async () => {
    let sent: BasketBatchItem[] = [];
    const updated: BasketsResponse = {
      ...baseBaskets,
      portfolios: [
        {
          ...rubPortfolio,
          baskets: [
            { ...liquidityBasket, targetWeight: 0.2, target: 0.2, drift: -0.08, state: 'hardBelow', deficitToTarget: 80_000 },
            { ...bondsBasket, targetWeight: 0.8, target: 0.8, drift: 0.08, state: 'hardAbove', deficitToTarget: -80_000 },
            ...rubPortfolio.baskets.slice(2),
          ],
        },
        usdPortfolio,
      ],
    };
    server.use(
      http.put('*/api/baskets', async ({ request }) => {
        sent = ((await request.json()) as { items: BasketBatchItem[] }).items;
        return HttpResponse.json(updated);
      }),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('targets-edit-rub'));
    // Текущие значения подставлены в ПРОЦЕНТАХ.
    expect(screen.getByTestId('target-input-liquidity')).toHaveValue('15');
    expect(screen.getByTestId('band-input-liquidity')).toHaveValue('2');

    await userEvent.clear(screen.getByTestId('target-input-liquidity'));
    await userEvent.type(screen.getByTestId('target-input-liquidity'), '20');
    await userEvent.clear(screen.getByTestId('target-input-bonds'));
    await userEvent.type(screen.getByTestId('target-input-bonds'), '80');
    expect(screen.getByTestId('targets-sum-rub')).toHaveTextContent('100,00%');
    await userEvent.click(screen.getByTestId('targets-save-rub'));

    await waitFor(() => expect(sent).toHaveLength(3)); // liquidity, bonds, temporary (без системных строк)
    const byId = Object.fromEntries(sent.map((i) => [i.id, i]));
    expect(byId[1].targetWeight).toBe(0.2);
    expect(byId[2].targetWeight).toBe(0.8);
    expect(byId[1].band).toBe(0.02);
    expect(byId[1].hardBand).toBe(0.05);
    expect(byId[4].targetWeight).toBeNull();
    // нередактируемые поля сохраняются как были
    expect(byId[1].fillRule).toBe('fixed');
    expect(byId[2].fillRule).toBe('engine');

    await waitFor(() => expect(screen.queryByTestId('targets-form-rub')).not.toBeInTheDocument());
    expect(screen.getByTestId('basket-target-liquidity')).toHaveTextContent('20,0%');
  });

  it('показывает текст ошибки бэкенда при 422 и оставляет форму открытой', async () => {
    server.use(
      http.put('*/api/baskets', () =>
        HttpResponse.json(
          { error: 'Сумма целей портфеля rub = 1.15 (доля), должна быть 1 ± 0.0001.', type: 'ValidationException' },
          { status: 422 },
        ),
      ),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('targets-edit-rub'));
    await userEvent.clear(screen.getByTestId('target-input-liquidity'));
    await userEvent.type(screen.getByTestId('target-input-liquidity'), '30');
    expect(screen.getByTestId('targets-sum-rub')).toHaveTextContent('должна быть 100%');
    await userEvent.click(screen.getByTestId('targets-save-rub'));

    expect(await screen.findByTestId('targets-error-rub')).toHaveTextContent('Сумма целей портфеля rub = 1.15');
    expect(screen.getByTestId('targets-form-rub')).toBeInTheDocument();
  });

  it('сброс к стартовым — с подтверждением, запрос только по своему портфелю', async () => {
    let url = '';
    server.use(
      http.post('*/api/baskets/reset-defaults', ({ request }) => {
        url = request.url;
        return HttpResponse.json(baseBaskets);
      }),
    );
    renderCapital();

    await userEvent.click(await screen.findByTestId('reset-rub'));
    expect(url).toBe(''); // без подтверждения запроса нет
    await userEvent.click(screen.getByTestId('reset-confirm-rub'));

    await waitFor(() => expect(url).toContain('/api/baskets/reset-defaults?portfolio=rub'));
  });

  it('ошибка загрузки — ErrorState, а не пустая страница', async () => {
    server.use(http.get('*/api/baskets', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderCapital();

    expect(await screen.findByTestId('capital-error')).toBeInTheDocument();
  });
});
