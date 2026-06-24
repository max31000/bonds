import { describe, it, expect, beforeEach } from 'vitest';
import { useAuthStore } from './useAuthStore';
import type { AuthUser } from './useAuthStore';

const testUser: AuthUser = {
  id: 1,
  telegramId: 123456789,
  firstName: 'Owner',
};

beforeEach(() => {
  useAuthStore.setState({ token: null, user: null });
  localStorage.clear();
});

describe('useAuthStore', () => {
  it('is initially not authenticated (token and user are null)', () => {
    const state = useAuthStore.getState();
    expect(state.token).toBeNull();
    expect(state.user).toBeNull();
  });

  it('setAuth sets token and user correctly', () => {
    useAuthStore.getState().setAuth('jwt-token-abc', testUser);
    const state = useAuthStore.getState();
    expect(state.token).toBe('jwt-token-abc');
    expect(state.user).toEqual(testUser);
  });

  it('logout clears token and user', () => {
    useAuthStore.getState().setAuth('jwt-token-abc', testUser);
    useAuthStore.getState().logout();
    const state = useAuthStore.getState();
    expect(state.token).toBeNull();
    expect(state.user).toBeNull();
  });

  it('isAuthenticated returns false when token is null', () => {
    const state = useAuthStore.getState();
    expect(state.isAuthenticated()).toBe(false);
  });

  it('isAuthenticated returns true after setAuth', () => {
    useAuthStore.getState().setAuth('jwt-token-abc', testUser);
    expect(useAuthStore.getState().isAuthenticated()).toBe(true);
  });

  it('isAuthenticated returns false after logout', () => {
    useAuthStore.getState().setAuth('jwt-token-abc', testUser);
    useAuthStore.getState().logout();
    expect(useAuthStore.getState().isAuthenticated()).toBe(false);
  });
});
