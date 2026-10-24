import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { AgentSection } from './AgentSection';
import type { AgentSettingsRequest, AgentState } from '../api/types';

const state = (over: Partial<AgentState> = {}): AgentState => ({
  enabled: false,
  model: 'sonnet',
  monthlyModel: 'opus',
  dailyRunLimit: 5,
  maxDailyRunLimit: 20,
  credentialOk: true,
  serverEnabled: true,
  runsToday: 0,
  runs: [],
  disclaimer: 'Ответы ИИ-агента — оценки, не инвестиционные рекомендации.',
  ...over,
});

function renderSection() {
  return render(
    <MantineProvider>
      <AgentSection />
    </MantineProvider>,
  );
}

describe('AgentSection (задача 68)', () => {
  it('без подключения Claude Code — подсказка и выключатель недоступен', async () => {
    server.use(http.get('*/api/agent', () => HttpResponse.json(state({ credentialOk: false }))));
    renderSection();

    expect(await screen.findByTestId('agent-no-credential')).toHaveTextContent('claude setup-token');
    expect(screen.getByTestId('agent-enabled')).toBeDisabled();
    expect(screen.getByTestId('agent-runs-empty')).toBeInTheDocument();
  });

  it('включение и сохранение отправляет настройки и показывает ответ сервера', async () => {
    let sent: AgentSettingsRequest | null = null;
    server.use(
      http.get('*/api/agent', () => HttpResponse.json(state())),
      http.put('*/api/agent/settings', async ({ request }) => {
        sent = (await request.json()) as AgentSettingsRequest;
        return HttpResponse.json(state({ enabled: true, dailyRunLimit: sent.dailyRunLimit }));
      }),
    );
    renderSection();

    await userEvent.click(await screen.findByTestId('agent-enabled'));
    await userEvent.click(screen.getByTestId('agent-save'));

    await waitFor(() => expect(sent).toEqual({ enabled: true, model: 'sonnet', monthlyModel: 'opus', dailyRunLimit: 5 }));
    await waitFor(() => expect(screen.getByTestId('agent-enabled')).toBeChecked());
  });

  it('ошибка 422 показывается текстом', async () => {
    server.use(
      http.get('*/api/agent', () => HttpResponse.json(state())),
      http.put('*/api/agent/settings', () => HttpResponse.json({ error: 'Сначала подключите Claude Code', type: 'ValidationException' }, { status: 422 })),
    );
    renderSection();

    await userEvent.click(await screen.findByTestId('agent-save'));
    expect(await screen.findByTestId('agent-save-error')).toHaveTextContent('Сначала подключите Claude Code');
  });

  it('история: текст агента выводится как текст, HTML не исполняется', async () => {
    server.use(
      http.get('*/api/agent', () =>
        HttpResponse.json(
          state({
            enabled: true,
            runsToday: 1,
            runs: [
              {
                id: 7,
                kind: 'OnDemand',
                model: 'sonnet',
                question: 'что с офертами?',
                started: new Date().toISOString(),
                finished: new Date().toISOString(),
                exitCode: 0,
                costUsd: 0.0234,
                tokens: 1000,
                resultText: '<img src=x onerror="alert(1)"><b>жирный</b>',
                error: null,
              },
            ],
          }),
        ),
      ),
    );
    renderSection();

    const text = await screen.findByTestId('agent-run-text-7');
    expect(text).toHaveTextContent('<img src=x onerror="alert(1)"><b>жирный</b>');
    expect(text.querySelector('img')).toBeNull();
    expect(text.querySelector('b')).toBeNull();
    expect(screen.getByTestId('agent-run-7')).toHaveTextContent('Вопрос (/ask)');
    expect(screen.getByTestId('agent-run-7')).toHaveTextContent('≈ $0.02');
    expect(screen.getByTestId('agent-runs-today')).toHaveTextContent('Сегодня запусков: 1 из 5');
  });
});
