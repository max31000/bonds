import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { ExecutionModeSection } from './ExecutionModeSection';
import type { ExecutionState } from '../api/types';

const observe = (over: Partial<ExecutionState> = {}): ExecutionState => ({
  mode: 'observe',
  globalEnabled: true,
  tradeCredentialOk: true,
  paused: false,
  live: false,
  reasons: ['Режим наблюдения (observe)'],
  ...over,
});

function renderSection() {
  return render(
    <MantineProvider>
      <ExecutionModeSection />
    </MantineProvider>,
  );
}

describe('ExecutionModeSection (задача 65)', () => {
  it('формулировка лимитов: лимит по сумме — только у авто-подтверждённых; у ручных — нет', async () => {
    server.use(http.get('*/api/settings/execution', () => HttpResponse.json(observe())));
    renderSection();

    const note = await screen.findByTestId('execution-limits-note');
    expect(note).toHaveTextContent('только для планов, подтверждённых автоматически');
    expect(note).toHaveTextContent('лимита по сумме нет');
    expect(screen.queryByTestId('execution-active-orders')).not.toBeInTheDocument();
  });

  it('активные заявки у брокера видны в любом режиме', async () => {
    server.use(http.get('*/api/settings/execution', () => HttpResponse.json(observe({ activeOrders: 1 }))));
    renderSection();
    expect(await screen.findByTestId('execution-active-orders')).toHaveTextContent('Есть активные заявки у брокера: 1');
  });

  it('без проверенного торгового токена включить нельзя', async () => {
    server.use(http.get('*/api/settings/execution', () => HttpResponse.json(observe({ tradeCredentialOk: false }))));
    renderSection();

    expect(await screen.findByTestId('execution-need-token')).toBeInTheDocument();
    expect(screen.getByTestId('execution-confirm-input')).toBeDisabled();
    expect(screen.getByTestId('execution-enable')).toBeDisabled();
  });

  it('включение требует ввести ровно «LIVE»; уходит mode=live + confirmation', async () => {
    let body: { mode: string; confirmation?: string } | null = null;
    server.use(
      http.get('*/api/settings/execution', () => HttpResponse.json(observe())),
      http.put('*/api/settings/execution-mode', async ({ request }) => {
        body = (await request.json()) as { mode: string; confirmation?: string };
        return HttpResponse.json({ ...observe(), mode: 'live', live: true, reasons: [] });
      }),
    );
    renderSection();

    const input = await screen.findByTestId('execution-confirm-input');
    await userEvent.type(input, 'live');
    expect(screen.getByTestId('execution-enable')).toBeDisabled();
    await userEvent.clear(input);
    await userEvent.type(input, 'LIVE');
    expect(screen.getByTestId('execution-enable')).toBeEnabled();
    await userEvent.click(screen.getByTestId('execution-enable'));

    await waitFor(() => expect(body).toEqual({ mode: 'live', confirmation: 'LIVE' }));
    expect(await screen.findByTestId('execution-effective')).toHaveTextContent('Заявки отправляются');
    expect(screen.getByTestId('execution-pause')).toBeInTheDocument();
  });

  it('live, но сервер выключен — показывает причину и что заявки не отправляются', async () => {
    server.use(
      http.get('*/api/settings/execution', () =>
        HttpResponse.json({ ...observe(), mode: 'live', globalEnabled: false, reasons: ['Исполнение выключено на сервере (Execution__Enabled=false)'] }),
      ),
    );
    renderSection();

    expect(await screen.findByTestId('execution-effective')).toHaveTextContent('Заявки не отправляются');
    expect(screen.getByTestId('execution-reasons')).toHaveTextContent('Execution__Enabled');
    expect(screen.getByTestId('execution-disable')).toBeInTheDocument();
  });

  it('ошибка сервера (422) показывается текстом', async () => {
    server.use(
      http.get('*/api/settings/execution', () => HttpResponse.json(observe())),
      http.put('*/api/settings/execution-mode', () =>
        HttpResponse.json({ error: 'Нужен проверенный торговый токен T-Invest', type: 'ValidationException' }, { status: 422 }),
      ),
    );
    renderSection();

    await userEvent.type(await screen.findByTestId('execution-confirm-input'), 'LIVE');
    await userEvent.click(screen.getByTestId('execution-enable'));
    expect(await screen.findByTestId('execution-error')).toHaveTextContent('торговый токен');
  });
});
