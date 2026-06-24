import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { apiClient } from './client';
import { useAuthStore } from '../store/useAuthStore';

describe('apiClient', () => {
  const originalLocation = window.location;

  beforeEach(() => {
    useAuthStore.setState({ token: 'stale-token', user: { id: 1, telegramId: 1 } });
    // jsdom не позволяет напрямую переопределить window.location.href через присвоение
    // в некоторых версиях — подменяем весь объект на тестовый дублёр.
    Object.defineProperty(window, 'location', {
      value: { ...originalLocation, href: '' },
      writable: true,
    });
  });

  afterEach(() => {
    Object.defineProperty(window, 'location', { value: originalLocation, writable: true });
  });

  it('logs out and redirects to /login on a 401 response', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ error: 'unauthorized' }, { status: 401 })),
    );

    await expect(apiClient.get('/positions')).rejects.toThrow('Требуется авторизация');

    expect(useAuthStore.getState().token).toBeNull();
    expect(useAuthStore.getState().user).toBeNull();
    expect(window.location.href).toContain('/login');
  });

  it('attaches the Authorization header from the auth store', async () => {
    let capturedAuth: string | null = null;
    server.use(
      http.get('*/api/echo-auth', ({ request }) => {
        capturedAuth = request.headers.get('Authorization');
        return HttpResponse.json({ ok: true });
      }),
    );

    await apiClient.get('/echo-auth');

    expect(capturedAuth).toBe('Bearer stale-token');
  });
});
