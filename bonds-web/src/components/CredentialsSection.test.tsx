import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, beforeEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { CredentialsSection } from './CredentialsSection';
import { useCredentialsStore } from '../store/useCredentialsStore';
import type { CredentialRow } from '../api/types';

function renderSection() {
  return render(
    <MantineProvider>
      <Notifications />
      <CredentialsSection />
    </MantineProvider>,
  );
}

const missing = (over: Partial<CredentialRow>): CredentialRow => ({
  provider: 'x',
  title: 'X',
  category: 'broker',
  fields: [{ name: 'token', title: 'Токен', isSecret: true }],
  unlocks: 'что-то',
  experimental: false,
  status: 'missing',
  statusMessage: null,
  last4: null,
  verifiedAt: null,
  meta: null,
  ...over,
});

const tinvestRead = (over: Partial<CredentialRow> = {}) =>
  missing({ provider: 'tinvest_read', title: 'T-Invest (чтение)', unlocks: 'синхронизация портфеля и операций', ...over });
const claude = (over: Partial<CredentialRow> = {}) =>
  missing({
    provider: 'claude_code',
    title: 'Claude Code',
    category: 'ai',
    fields: [{ name: 'oauthToken', title: 'OAuth-токен', isSecret: true }],
    unlocks: 'ИИ-агенты: мониторинг и разборы',
    ...over,
  });

const SECRET = 'sk-ant-oat01-VeryS3cretTokenValue-ZZ99';

describe('CredentialsSection', () => {
  beforeEach(() => {
    useCredentialsStore.setState({ credentials: [], capabilities: null, isLoading: false, error: null });
  });

  it('renders a card per provider from the response, grouped into Brokers and AI', async () => {
    server.use(
      http.get('*/api/credentials', () =>
        HttpResponse.json([
          tinvestRead({ status: 'ok', last4: '1234', verifiedAt: '2026-10-01T09:30:00Z' }),
          missing({ provider: 'tinvest_trade', title: 'T-Invest (торговля)', experimental: true, unlocks: 'исполнение подтверждённых планов T-Invest (выключено по умолчанию)' }),
          claude({ status: 'invalid', statusMessage: 'Нужно ввести заново', last4: 'ZZ99' }),
        ]),
      ),
    );

    renderSection();

    await waitFor(() => expect(screen.getByTestId('credential-tinvest_read')).toBeInTheDocument());
    expect(screen.getByTestId('credentials-group-broker')).toHaveTextContent('Брокеры');
    expect(screen.getByTestId('credentials-group-ai')).toHaveTextContent('ИИ');
    expect(screen.getByTestId('credentials-group-broker')).toContainElement(screen.getByTestId('credential-tinvest_trade'));
    expect(screen.getByTestId('credentials-group-ai')).toContainElement(screen.getByTestId('credential-claude_code'));

    expect(screen.getByTestId('credential-tinvest_read')).toHaveTextContent('Подключено');
    expect(screen.getByTestId('credential-tinvest_read-mask')).toHaveTextContent('••••1234');
    expect(screen.getByTestId('credential-tinvest_read')).toHaveTextContent('Включает: синхронизация портфеля и операций');
    expect(screen.getByTestId('credential-tinvest_read')).toHaveTextContent('Только на чтение');
    expect(screen.getByTestId('credential-tinvest_trade')).toHaveTextContent('Не задано');
    expect(screen.getByTestId('credential-tinvest_trade')).toHaveTextContent('эксперимент');
    expect(screen.getByTestId('credential-claude_code')).toHaveTextContent('Ошибка: Нужно ввести заново');
    expect(screen.getByTestId('credential-claude_code')).toHaveTextContent('claude setup-token');
    expect(screen.getByTestId('credential-tinvest_read-edit')).toHaveTextContent('Заменить');
    expect(screen.getByTestId('credential-tinvest_trade-edit')).toHaveTextContent('Задать');
    expect(screen.queryByTestId('credential-tinvest_trade-delete')).not.toBeInTheDocument();
  });

  it('keeps the form open and shows the backend message on 422, sending the entered fields', async () => {
    let putBody: unknown = null;
    server.use(
      http.get('*/api/credentials', () => HttpResponse.json([claude()])),
      http.put('*/api/credentials/claude_code', async ({ request }) => {
        putBody = await request.json();
        return HttpResponse.json(
          { error: 'Ожидается токен из `claude setup-token`', type: 'ValidationException' },
          { status: 422 },
        );
      }),
    );

    renderSection();
    await waitFor(() => expect(screen.getByTestId('credential-claude_code-edit')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('credential-claude_code-edit'));
    await userEvent.type(screen.getByTestId('credential-claude_code-input-oauthToken'), 'wrong-token');
    await userEvent.click(screen.getByTestId('credential-claude_code-save'));

    await waitFor(() => expect(screen.getByTestId('credential-claude_code-error')).toHaveTextContent('Ожидается токен'));
    expect(putBody).toEqual({ fields: { oauthToken: 'wrong-token' } });
    expect(screen.getByTestId('credential-claude_code-form')).toBeInTheDocument();
    expect(screen.getByTestId('credential-claude_code')).toHaveTextContent('Не задано');
  });

  it('closes the form after success, shows only the mask and never renders the secret', async () => {
    let saved = false;
    server.use(
      http.get('*/api/credentials', () =>
        HttpResponse.json([claude(saved ? { status: 'ok', last4: 'ZZ99', verifiedAt: '2026-10-23T10:00:00Z' } : {})]),
      ),
      http.put('*/api/credentials/claude_code', () => {
        saved = true;
        return HttpResponse.json(claude({ status: 'ok', last4: 'ZZ99', verifiedAt: '2026-10-23T10:00:00Z' }));
      }),
    );

    renderSection();
    await waitFor(() => expect(screen.getByTestId('credential-claude_code-edit')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('credential-claude_code-edit'));
    const input = screen.getByTestId('credential-claude_code-input-oauthToken');
    expect(input).toHaveAttribute('type', 'password');
    await userEvent.type(input, SECRET);
    await userEvent.click(screen.getByTestId('credential-claude_code-save'));

    await waitFor(() => expect(screen.getByTestId('credential-claude_code')).toHaveTextContent('Подключено'));
    expect(screen.queryByTestId('credential-claude_code-form')).not.toBeInTheDocument();
    expect(screen.getByTestId('credential-claude_code-mask')).toHaveTextContent('••••ZZ99');
    expect(document.body.innerHTML).not.toContain(SECRET);
    expect(document.body.innerHTML).not.toContain('VeryS3cretTokenValue');

    // после повторного открытия формы поле пустое — секрет не восстанавливается
    await userEvent.click(screen.getByTestId('credential-claude_code-edit'));
    expect(screen.getByTestId('credential-claude_code-input-oauthToken')).toHaveValue('');
  });

  it('asks for confirmation and calls DELETE, then reloads the list', async () => {
    let deleted = false;
    let deleteCalls = 0;
    server.use(
      http.get('*/api/credentials', () =>
        HttpResponse.json([claude(deleted ? {} : { status: 'ok', last4: 'ZZ99' })]),
      ),
      http.delete('*/api/credentials/claude_code', () => {
        deleted = true;
        deleteCalls += 1;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderSection();
    await waitFor(() => expect(screen.getByTestId('credential-claude_code-delete')).toBeInTheDocument());

    await userEvent.click(screen.getByTestId('credential-claude_code-delete'));
    expect(deleteCalls).toBe(0); // без подтверждения запрос не уходит
    await userEvent.click(screen.getByTestId('credential-claude_code-delete-yes'));

    await waitFor(() => expect(deleteCalls).toBe(1));
    await waitFor(() => expect(screen.getByTestId('credential-claude_code')).toHaveTextContent('Не задано'));
  });
  it('renders the Freedom card from the registry response with its hint and both key fields', async () => {
    server.use(
      http.get('*/api/credentials', () =>
        HttpResponse.json([
          missing({
            provider: 'freedom',
            title: 'Freedom (Tradernet)',
            fields: [
              { name: 'publicKey', title: 'Публичный ключ (API key)', isSecret: true },
              { name: 'privateKey', title: 'Секретный ключ (Secret key)', isSecret: true },
            ],
            unlocks: 'синхронизация портфеля Freedom (только чтение)',
          }),
        ]),
      ),
    );

    renderSection();

    await waitFor(() => expect(screen.getByTestId('credential-freedom')).toBeInTheDocument());
    expect(screen.getByTestId('credentials-group-broker')).toContainElement(screen.getByTestId('credential-freedom'));
    expect(screen.getByTestId('credential-freedom')).toHaveTextContent('Freedom (Tradernet)');
    expect(screen.getByTestId('credential-freedom')).toHaveTextContent('веб-терминале Freedom: профиль → API-ключи');
    await userEvent.click(screen.getByTestId('credential-freedom-edit'));
    expect(screen.getByTestId('credential-freedom-input-publicKey')).toBeInTheDocument();
    expect(screen.getByTestId('credential-freedom-input-privateKey')).toBeInTheDocument();
  });

  it('freedom_trade card says the trade right is verified on the first order', async () => {
    server.use(
      http.get('*/api/credentials', () =>
        HttpResponse.json([
          missing({
            provider: 'freedom_trade',
            title: 'Freedom (торговля)',
            experimental: true,
            fields: [
              { name: 'publicKey', title: 'Публичный ключ', isSecret: true },
              { name: 'privateKey', title: 'Секретный ключ', isSecret: true },
            ],
          }),
        ]),
      ),
    );
    renderSection();
    expect(await screen.findByText(/Торговое право проверится на первой заявке/)).toBeInTheDocument();
    expect(screen.getByText(/\.ITS исполнитель не отправляет никогда/)).toBeInTheDocument();
  });

  it('tinvest_trade hint describes the executor, not the stale «phase 3, unused» text', async () => {
    server.use(
      http.get('*/api/credentials', () =>
        HttpResponse.json([missing({ provider: 'tinvest_trade', title: 'T-Invest (торговля)', experimental: true, unlocks: 'исполнение подтверждённых планов T-Invest' })]),
      ),
    );
    renderSection();
    const card = await screen.findByTestId('credential-tinvest_trade');
    expect(card).toHaveTextContent('только для исполнителя подтверждённых планов T-Invest');
    expect(card).not.toHaveTextContent('нигде не используется');
    expect(card).not.toHaveTextContent('фазе 3');
  });
});
