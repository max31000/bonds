import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { executingPlan, failedBondPlan, livePlansResponse, plansResponse, sweepPlan } from '../test/plansFixtures';
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
