import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, beforeEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { reloadMe } from '@mvv42/kit/platform';
import App from './App';
import { server } from './test/msw-handlers';

describe('App', () => {
  beforeEach(async () => {
    localStorage.clear();
    window.history.pushState({}, '', '/');
    await reloadMe();
  });

  it('asks an anonymous visitor to sign in through the shared login', async () => {
    server.use(http.get('*/auth/api/me', () => HttpResponse.json({ user: null, apps: [] })));
    await reloadMe();

    render(<App />);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Войти через Telegram' })).toBeInTheDocument());
  });

  it('removes the legacy bonds-auth token key on startup', async () => {
    localStorage.setItem('bonds-auth', JSON.stringify({ state: { token: 'old' } }));

    render(<App />);

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Обзор' })).toBeInTheDocument());
    expect(localStorage.getItem('bonds-auth')).toBeNull();
  });

  it('renders the dashboard for the signed-in owner', async () => {
    render(<App />);

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Обзор' })).toBeInTheDocument());
  });

  it('renders the positions screen at /positions', async () => {
    window.history.pushState({}, '', '/positions');
    render(<App />);

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Позиции' })).toBeInTheDocument());
  });
});
