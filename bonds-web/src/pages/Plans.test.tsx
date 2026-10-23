import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { approvedPlan, autoPlan, inflowPlan, plansResponse, stalePlan, sweepPlan, withdrawPlan } from '../test/plansFixtures';
import { Plans } from './Plans';
import { usePlansStore } from '../store/usePlansStore';

function renderPlans() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Plans />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('Plans — страница «Планы» (задача 63, режим наблюдения)', () => {
  beforeEach(() => {
    usePlansStore.setState({ data: null, isLoading: false, error: null, decidingId: null });
  });

  it('всегда показывает баннер «Режим наблюдения: сделки не отправляются» — и при пустом списке', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(plansResponse([]))));
    renderPlans();

    expect(screen.getByTestId('dryrun-banner')).toHaveTextContent('Режим наблюдения: сделки не отправляются');
    expect(await screen.findByTestId('plans-empty')).toBeInTheDocument();
    expect(screen.getByTestId('disclaimer')).toHaveTextContent('не рекомендация');
  });

  it('рисует карточку плана: правило, причина, статус, итоги и строки заявки (лоты, лимитная цена, сумма, комиссия)', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(plansResponse([sweepPlan]))));
    renderPlans();

    expect(await screen.findByTestId('plan-11')).toBeInTheDocument();
    expect(screen.getByText('Парковка свободных денег')).toBeInTheDocument();
    expect(screen.getByTestId('plan-11-trigger')).toHaveTextContent('Свободные рубли T-Invest');
    expect(screen.getByTestId('plan-11-status')).toHaveTextContent('Ждёт решения');
    expect(screen.getByTestId('plan-11-cash-before')).toHaveTextContent(/7\s340,00\sRUB/);
    expect(screen.getByTestId('plan-11-cash-after')).toHaveTextContent(/8,92\sRUB/);
    expect(screen.getByTestId('plan-11-line-1-qty')).toHaveTextContent('26 лот.');
    expect(screen.getByTestId('plan-11-line-1-price')).toHaveTextContent(/166,58\sRUB/);
    expect(screen.getByTestId('plan-11-line-1-amount')).toHaveTextContent(/4\s331,08\sRUB/);
    expect(screen.getByTestId('plan-11-approve')).toBeEnabled();
    expect(screen.getByTestId('plan-11-reject')).toBeEnabled();
  });

  it('облигационная строка: цена в ПУНКТАХ и рубли; пропущенные бумаги с причиной', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(plansResponse([inflowPlan]))));
    renderPlans();

    expect(await screen.findByTestId('plan-12-line-2-price')).toHaveTextContent(/99,95\sп\.\s\(999,50\sRUB\)/);
    expect(screen.getByTestId('plan-12-skipped')).toHaveTextContent('LQDT — нет сделки с известной комиссией');
    expect(screen.getByText('Распределение пополнения')).toBeInTheDocument();
  });

  it('вывод: «к выводу после налога и комиссии», налог в строке продажи и предупреждение об оценке налога', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(plansResponse([withdrawPlan]))));
    renderPlans();

    expect(await screen.findByTestId('plan-13-net')).toHaveTextContent(/К выводу после налога и комиссии:\s*2\s131,02\sRUB\sиз запрошенных 2\s000,00\sRUB/);
    expect(screen.getByTestId('plan-13-line-1')).toHaveTextContent('Продажа');
    expect(screen.getByTestId('plan-13-line-1')).toHaveTextContent(/28,02\sRUB/);
    expect(screen.getByText(/не FIFO/)).toBeInTheDocument();
  });

  it('«Подтвердить» шлёт POST approve, статус становится «Подтверждён» с пометкой «не исполнен: dry-run», кнопки пропадают', async () => {
    let approvedId = 0;
    server.use(
      http.get('*/api/plans', () => HttpResponse.json(plansResponse([sweepPlan]))),
      http.post('*/api/plans/:id/approve', ({ params }) => {
        approvedId = Number(params.id);
        return HttpResponse.json({ ...approvedPlan, id: 11 });
      }),
    );
    renderPlans();

    await userEvent.click(await screen.findByTestId('plan-11-approve'));

    await waitFor(() => expect(screen.getByTestId('plan-11-status')).toHaveTextContent('Подтверждён'));
    expect(approvedId).toBe(11);
    expect(screen.getByTestId('plan-11-note')).toHaveTextContent('не исполнен: dry-run');
    expect(screen.queryByTestId('plan-11-approve')).not.toBeInTheDocument();
    // Подтверждённый план переехал из «Ждут решения» в «История».
    expect(within(screen.getByTestId('plans-history')).getByTestId('plan-11')).toBeInTheDocument();
  });

  it('«Отклонить» шлёт POST reject и показывает «Отклонён»', async () => {
    let rejected = false;
    server.use(
      http.get('*/api/plans', () => HttpResponse.json(plansResponse([sweepPlan]))),
      http.post('*/api/plans/:id/reject', () => {
        rejected = true;
        return HttpResponse.json({ ...sweepPlan, status: 'Rejected', decidedVia: 'web', decidedAt: '2026-10-21T09:06:00Z' });
      }),
    );
    renderPlans();

    await userEvent.click(await screen.findByTestId('plan-11-reject'));

    await waitFor(() => expect(screen.getByTestId('plan-11-status')).toHaveTextContent('Отклонён'));
    expect(rejected).toBe(true);
  });

  it('409 (план уже решён или просрочен) показывает текст ошибки бэкенда и обновляет список', async () => {
    let loads = 0;
    server.use(
      http.get('*/api/plans', () => {
        loads += 1;
        return HttpResponse.json(plansResponse([sweepPlan]));
      }),
      http.post('*/api/plans/:id/approve', () =>
        HttpResponse.json({ error: 'План просрочен и больше не может быть подтверждён.', type: 'PlanExpired' }, { status: 409 }),
      ),
    );
    renderPlans();

    await userEvent.click(await screen.findByTestId('plan-11-approve'));

    expect(await screen.findByTestId('plan-11-error')).toHaveTextContent('просрочен');
    await waitFor(() => expect(loads).toBeGreaterThanOrEqual(2));
  });

  it('авто-подтверждённый план — без кнопок решения; построенный вне часов торгов помечен как «цены могли устареть»', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(plansResponse([autoPlan, stalePlan]))));
    renderPlans();

    expect(await screen.findByTestId('plan-15-status')).toHaveTextContent('Авто-подтверждён');
    expect(screen.queryByTestId('plan-15-approve')).not.toBeInTheDocument();
    expect(screen.getByTestId('plan-16-stale')).toHaveTextContent('вне часов торгов');
  });

  it('ошибка загрузки — ErrorState, баннер остаётся', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));
    renderPlans();

    expect(await screen.findByTestId('plans-error')).toBeInTheDocument();
    expect(screen.getByTestId('dryrun-banner')).toBeInTheDocument();
  });
});
