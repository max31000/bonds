import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { executingPlan, failedBondPlan, livePlansResponse, plansResponse, sweepPlan, tmonLine } from '../test/plansFixtures';
import type { Plan } from '../api/types';
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

describe('Plans — исполнение (задача 65)', () => {
  beforeEach(() => {
    usePlansStore.setState({ data: null, isLoading: false, error: null, decidingId: null });
  });

  it('dry-run ответ — баннер «Режим наблюдения», без «Остановить» и без статусов заявок', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(plansResponse([sweepPlan]))));
    renderPlans();
    expect(await screen.findByTestId('plan-11')).toBeInTheDocument();
    expect(screen.getByTestId('dryrun-banner')).toHaveTextContent('Режим наблюдения');
    expect(screen.queryByTestId('live-banner')).not.toBeInTheDocument();
    expect(screen.queryByTestId('execution-stop')).not.toBeInTheDocument();
    expect(screen.queryByTestId('plan-11-line-1-exec')).not.toBeInTheDocument();
  });

  it('live — баннер «Исполнение включено», «Остановить» ставит паузу и перечитывает планы', async () => {
    let paused: boolean | null = null;
    let live = true;
    server.use(
      http.get('*/api/plans', () => HttpResponse.json(live ? livePlansResponse([executingPlan()]) : plansResponse([executingPlan()]))),
      http.put('*/api/settings/execution-pause', async ({ request }) => {
        paused = ((await request.json()) as { paused: boolean }).paused;
        live = false;
        return HttpResponse.json({ mode: 'live', globalEnabled: true, tradeCredentialOk: true, paused: true, live: false, reasons: ['Пауза (/stop)'] });
      }),
    );
    renderPlans();

    expect(await screen.findByTestId('live-banner')).toHaveTextContent('Исполнение включено');
    await userEvent.click(screen.getByTestId('execution-stop'));

    await waitFor(() => expect(paused).toBe(true));
    expect(await screen.findByTestId('dryrun-banner')).toBeInTheDocument();
  });

  it('live только у Freedom — баннер называет брокеров отдельно', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json({ ...livePlansResponse([sweepPlan]), brokers: { tinvest: false, freedom: true } })));
    renderPlans();
    const banner = await screen.findByTestId('live-banner');
    expect(banner).toHaveTextContent('отправляются брокеру Freedom');
    expect(banner).toHaveTextContent('T-Invest — заявки не отправляются');
  });

  it('live у обоих брокеров — оба в баннере; только T-Invest — Freedom не отправляется', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json({ ...livePlansResponse([sweepPlan]), brokers: { tinvest: true, freedom: true } })));
    const { unmount } = renderPlans();
    expect(await screen.findByTestId('live-banner')).toHaveTextContent('брокерам T-Invest и Freedom');
    unmount();

    usePlansStore.setState({ data: null, isLoading: false, error: null, decidingId: null });
    server.use(http.get('*/api/plans', () => HttpResponse.json({ ...livePlansResponse([sweepPlan]), brokers: { tinvest: true, freedom: false } })));
    renderPlans();
    const banner = await screen.findByTestId('live-banner');
    expect(banner).toHaveTextContent('брокеру T-Invest');
    expect(banner).toHaveTextContent('Freedom — заявки не отправляются');
  });

  it('показывает статусы заявок по строкам: исполнено N из M, попытка; ошибка проверки — с причиной; облигация — в пунктах', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json(livePlansResponse([executingPlan(), failedBondPlan()]))));
    renderPlans();

    expect(await screen.findByTestId('plan-21-line-1-exec-status')).toHaveTextContent('Исполнена частично');
    expect(screen.getByTestId('plan-21-line-1-exec')).toHaveTextContent('исполнено 10 из 26 лот.');
    expect(screen.getByTestId('plan-21-line-1-exec')).toHaveTextContent('попытка 2');
    expect(screen.getByTestId('plan-21-status')).toHaveTextContent('Исполняется');
    expect(screen.getByTestId('plan-22-line-2-exec-status')).toHaveTextContent('Не отправлена (проверка)');
    expect(screen.getByTestId('plan-22-line-2-exec-error')).toHaveTextContent('Нет свежей котировки');
    expect(screen.getByTestId('plan-22-line-2-exec')).toHaveTextContent(/99,95\sп\./);
    expect(screen.getByTestId('plan-22-status')).toHaveTextContent('Не исполнен');
  });

  it('активные заявки у брокера — баннер даже в режиме наблюдения', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json({ ...plansResponse([executingPlan()]), activeOrders: 2 })));
    renderPlans();

    expect(await screen.findByTestId('active-orders-banner')).toHaveTextContent('Есть активные заявки у брокера: 2');
    expect(screen.getByTestId('dryrun-banner')).toBeInTheDocument();
  });

  it('нет активных заявок — баннера нет', async () => {
    server.use(http.get('*/api/plans', () => HttpResponse.json({ ...livePlansResponse([executingPlan()]), activeOrders: 0 })));
    renderPlans();
    expect(await screen.findByTestId('live-banner')).toBeInTheDocument();
    expect(screen.queryByTestId('active-orders-banner')).not.toBeInTheDocument();
  });

  it('unknown_state — отдельная подпись «Статус заявки неизвестен — проверьте терминал»; «не снята автоматически» — предупреждение', async () => {
    const plan = executingPlan();
    const base = plan.lines[0].orders[1];
    const unknown: Plan = {
      ...plan,
      id: 23,
      status: 'Failed',
      lines: [{ ...plan.lines[0], executionStatus: 'Failed', filledLots: 0, orders: [{ ...base, status: 'Failed', lotsFilled: 0, errorCode: 'unknown_state', errorMessage: 'Состояние не подтвердилось' }] }],
    };
    const stuck: Plan = {
      ...plan,
      id: 24,
      needsAttention: true,
      lines: [{ ...plan.lines[0], executionStatus: 'Submitted', orders: [{ ...base, status: 'Submitted', needsAttention: true }] }],
    };
    server.use(http.get('*/api/plans', () => HttpResponse.json(livePlansResponse([unknown, stuck]))));
    renderPlans();

    expect(await screen.findByTestId('plan-23-line-1-exec-status')).toHaveTextContent('Статус заявки неизвестен — проверьте терминал');
    expect(screen.getByTestId('plan-24-line-1-exec-attention')).toHaveTextContent('снимите её в терминале брокера');
    expect(screen.getByTestId('plan-24-attention')).toBeInTheDocument();
    expect(screen.getByTestId('plan-24-status')).toHaveTextContent('Исполняется');
  });

  it('живой план: «Подтвердить» сначала показывает список настоящих заявок, approve — только после «Отправить заявки»', async () => {
    let approved = 0;
    const pending: Plan = { ...sweepPlan, id: 31, dryRun: false, lines: [tmonLine] };
    server.use(
      http.get('*/api/plans', () => HttpResponse.json(livePlansResponse([pending]))),
      http.post('*/api/plans/:id/approve', ({ params }) => {
        approved = Number(params.id);
        return HttpResponse.json({ ...pending, status: 'Approved' });
      }),
    );
    renderPlans();

    await userEvent.click(await screen.findByTestId('plan-31-approve'));
    const orders = await screen.findByTestId('plan-31-confirm-live-orders');
    expect(orders).toHaveTextContent('Покупка TMON@ — 26 лот.');
    expect(approved).toBe(0);

    await userEvent.click(screen.getByTestId('plan-31-confirm-live-cancel'));
    expect(approved).toBe(0);

    await userEvent.click(screen.getByTestId('plan-31-approve'));
    await userEvent.click(await screen.findByTestId('plan-31-confirm-live-submit'));
    await waitFor(() => expect(approved).toBe(31));
  });

  it('dry-run план: «Подтвердить» — без диалога', async () => {
    let approved = 0;
    server.use(
      http.get('*/api/plans', () => HttpResponse.json(plansResponse([sweepPlan]))),
      http.post('*/api/plans/:id/approve', ({ params }) => {
        approved = Number(params.id);
        return HttpResponse.json({ ...sweepPlan, status: 'Approved' });
      }),
    );
    renderPlans();

    await userEvent.click(await screen.findByTestId('plan-11-approve'));
    await waitFor(() => expect(approved).toBe(11));
    expect(screen.queryByTestId('plan-11-confirm-live-orders')).not.toBeInTheDocument();
  });

  it('«Отменить заявки» есть только у исполняемого плана и вызывает cancel-execution', async () => {
    let cancelled: string | null = null;
    server.use(
      http.get('*/api/plans', () => HttpResponse.json(livePlansResponse([executingPlan(), failedBondPlan()]))),
      http.post('*/api/plans/:id/cancel-execution', ({ params }) => {
        cancelled = String(params.id);
        return HttpResponse.json(executingPlan());
      }),
    );
    renderPlans();

    await userEvent.click(await screen.findByTestId('plan-21-cancel-execution'));
    await waitFor(() => expect(cancelled).toBe('21'));
    expect(screen.queryByTestId('plan-22-cancel-execution')).not.toBeInTheDocument();
  });
});
