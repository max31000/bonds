import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';
import App from './App';
import { useAuthStore } from './store/useAuthStore';

/**
 * Мок fetch, маршрутизирующий по URL — дашборд (plan/18) дёргает несколько эндпоинтов
 * параллельно (positions/xirr/cashflow/live/...), каждый со своей формой ответа; единый
 * "всегда { positions: [] }" мок ломает виджеты, ожидающие другие поля (например
 * PortfolioIntradayChart ожидает `points`).
 */
function mockAuthenticatedFetch() {
  return vi.fn().mockImplementation((url: string) => {
    const body = ((): unknown => {
      if (url.includes('/live/portfolio-intraday')) return { points: [] };
      if (url.includes('/live/positions')) return { positions: [], totalMarketValueRub: 0, asOfUtc: new Date().toISOString() };
      if (url.includes('/cashflow')) return { byMonth: [], byPosition: [], principalReleases: [], nextPayments: [], disclaimer: '' };
      if (url.includes('/analytics/xirr')) return { currentXirr: null, history: [], disclaimer: '' };
      if (url.includes('/analytics/composition')) {
        return { totalMarketValueRub: 0, byIssuer: [], bySector: [], byCouponType: [], byDurationBucket: [], disclaimer: '' };
      }
      if (url.includes('/signals')) return { signals: [] };
      if (url.includes('/positions')) return { positions: [], disclaimer: '' };
      return {};
    })();
    return Promise.resolve({ ok: true, status: 200, json: async () => body });
  });
}

describe('App', () => {
  beforeEach(() => {
    useAuthStore.setState({ token: null, user: null });
    localStorage.clear();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('redirects unauthenticated users to the login screen', async () => {
    render(<App />);
    await waitFor(() => expect(screen.getByText('Войти')).toBeInTheDocument());
  });

  it('renders the dashboard screen once authenticated', async () => {
    vi.stubGlobal('fetch', mockAuthenticatedFetch());

    useAuthStore.getState().setAuth('test-token', {
      id: 1,
      telegramId: 123456789,
      firstName: 'Owner',
    });

    render(<App />);

    await waitFor(() =>
      expect(screen.getByRole('heading', { name: 'Обзор' })).toBeInTheDocument(),
    );
  });

  it('renders the positions table screen at /positions', async () => {
    vi.stubGlobal('fetch', mockAuthenticatedFetch());

    useAuthStore.getState().setAuth('test-token', {
      id: 1,
      telegramId: 123456789,
      firstName: 'Owner',
    });

    window.history.pushState({}, '', '/positions');
    render(<App />);

    await waitFor(() =>
      expect(screen.getByRole('heading', { name: 'Позиции' })).toBeInTheDocument(),
    );
  });
});
