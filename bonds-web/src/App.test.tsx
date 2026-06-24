import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';
import App from './App';
import { useAuthStore } from './store/useAuthStore';

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

  it('renders the coming-soon placeholder once authenticated', async () => {
    useAuthStore.getState().setAuth('test-token', {
      id: 1,
      telegramId: 123456789,
      firstName: 'Owner',
    });

    render(<App />);

    await waitFor(() =>
      expect(screen.getByText('MVP в разработке')).toBeInTheDocument(),
    );
  });
});
