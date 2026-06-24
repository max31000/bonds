import { render, screen, waitFor, act } from '@testing-library/react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import Login from './Login';
import { useAuthStore } from '../store/useAuthStore';

function renderLogin() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Login />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('Login', () => {
  beforeEach(() => {
    useAuthStore.setState({ token: null, user: null });
    localStorage.clear();
  });

  afterEach(() => {
    delete window.onTelegramAuth;
  });

  it('renders the Telegram login prompt', () => {
    renderLogin();

    expect(screen.getByText('Войти')).toBeInTheDocument();
  });

  it('stores the session after a successful Telegram callback', async () => {
    renderLogin();

    // Скрипт telegram-widget.js не выполняется в jsdom — вызываем зарегистрированный
    // глобальный колбэк напрямую, как это сделал бы реальный виджет после авторизации.
    await waitFor(() => expect(window.onTelegramAuth).toBeDefined());

    await act(async () => {
      await window.onTelegramAuth!({
        id: 123456789,
        first_name: 'Owner',
        auth_date: Math.floor(Date.now() / 1000),
        hash: 'irrelevant-in-mocked-response',
      });
    });

    await waitFor(() => expect(useAuthStore.getState().token).toBe('mock-jwt-token'));
    expect(useAuthStore.getState().user?.telegramId).toBe(123456789);
  });

  it('shows an error notification when the backend rejects the login', async () => {
    server.use(
      http.post('*/api/auth/telegram', () =>
        HttpResponse.json({ error: 'Доступ запрещён' }, { status: 403 }),
      ),
    );

    renderLogin();

    await waitFor(() => expect(window.onTelegramAuth).toBeDefined());

    await act(async () => {
      await window.onTelegramAuth!({
        id: 999,
        auth_date: Math.floor(Date.now() / 1000),
        hash: 'irrelevant',
      });
    });

    // Авторизация не должна сохраниться при ошибке backend'а
    await waitFor(() => expect(useAuthStore.getState().token).toBeNull());
  });
});
