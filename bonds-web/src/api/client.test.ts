import { describe, it, expect, beforeEach, afterEach, onTestFinished, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { ApiError } from '@mvv42/kit/platform';
import { server } from '../test/msw-handlers';
import { apiClient, __resetLoginRequestForTests } from './client';
import { useAccessStore } from '../store/useAccessStore';

describe('apiClient', () => {
  const originalLocation = window.location;
  let assign: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    __resetLoginRequestForTests();
    useAccessStore.getState().reset();
    assign = vi.fn();
    Object.defineProperty(window, 'location', {
      value: { ...originalLocation, pathname: '/bonds/positions', search: '', hash: '', assign },
      writable: true,
    });
  });

  afterEach(() => {
    Object.defineProperty(window, 'location', { value: originalLocation, writable: true });
  });

  it('sends cookies, never a Bearer header', async () => {
    let auth: string | null = 'unset';
    server.use(
      http.get('*/api/echo', ({ request }) => {
        auth = request.headers.get('Authorization');
        return HttpResponse.json({ ok: true });
      }),
    );

    await expect(apiClient.get('/echo')).resolves.toEqual({ ok: true });
    expect(auth).toBeNull();
  });

  it('asks the shell for a login after a final 401 (refresh failed)', async () => {
    server.use(http.get('*/api/positions', () => HttpResponse.json({ error: 'unauthorized' }, { status: 401 })));

    await expect(apiClient.get('/positions')).rejects.toBeInstanceOf(ApiError);

    expect(assign).toHaveBeenCalledTimes(1);
    expect(assign.mock.calls[0][0]).toContain('/?login=1&return=');
  });

  it('retries once after a successful session refresh', async () => {
    // jsdom's AbortSignal is not the one Node's fetch accepts: the kit's refresh timeout signal would make
    // the refresh fetch reject under vitest. Without AbortSignal.timeout the kit sends no signal.
    const timeout = AbortSignal.timeout;
    Object.defineProperty(AbortSignal, 'timeout', { value: undefined, configurable: true, writable: true });
    onTestFinished(() => {
      Object.defineProperty(AbortSignal, 'timeout', { value: timeout, configurable: true, writable: true });
    });
    let calls = 0;
    server.use(
      http.post('*/auth/api/refresh', () => new HttpResponse(null, { status: 204 })),
      http.get('*/api/positions', () => {
        calls += 1;
        return calls === 1
          ? HttpResponse.json({ error: 'expired' }, { status: 401 })
          : HttpResponse.json({ positions: [], disclaimer: '' });
      }),
    );

    await expect(apiClient.get('/positions')).resolves.toEqual({ positions: [], disclaimer: '' });
    expect(calls).toBe(2);
    expect(assign).not.toHaveBeenCalled();
  });

  it('flags «no access» on 403 and keeps the server message', async () => {
    server.use(http.get('*/api/positions', () => HttpResponse.json({ error: 'forbidden' }, { status: 403 })));

    await expect(apiClient.get('/positions')).rejects.toThrow('forbidden');

    expect(useAccessStore.getState().denied?.status).toBe(403);
    expect(assign).not.toHaveBeenCalled();
  });

  it('surfaces the backend error text for other failures', async () => {
    server.use(
      http.put('*/api/settings', () =>
        HttpResponse.json({ error: 'Ставка должна быть меньше 5%', type: 'ValidationException' }, { status: 422 }),
      ),
    );

    await expect(apiClient.put('/settings', { commissionRateOverride: 0.1 })).rejects.toThrow('Ставка должна быть меньше 5%');
  });
});
