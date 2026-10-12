import { create } from 'zustand';
import type { ApiError } from '@mvv42/kit/platform';

interface AccessState {
  /** 403 от API: вход выполнен, но аккаунт не владелец bonds — показываем «Нет доступа» вместо экранов. */
  denied: ApiError | null;
  deny: (error: ApiError) => void;
  reset: () => void;
}

export const useAccessStore = create<AccessState>((set) => ({
  denied: null,
  deny: (error) => set({ denied: error }),
  reset: () => set({ denied: null }),
}));
