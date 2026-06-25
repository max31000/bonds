import { create } from 'zustand';
import { fetchSettings, updateSettings, updateTInvestToken } from '../api/settings';
import type { SettingsResponse, SettingsUpdateRequest } from '../api/types';

interface SettingsStore {
  settings: SettingsResponse | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  save: (body: SettingsUpdateRequest) => Promise<boolean>;
  /** Токен никогда не хранится в сторе дольше момента отправки (см. план 09c §B.8). */
  saveToken: (token: string) => Promise<boolean>;
}

/** Кэш настроек (GET/PUT /api/settings). Без `persist` — серверные данные, не клиентский UI-стейт. */
export const useSettingsStore = create<SettingsStore>()((set) => ({
  settings: null,
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      const settings = await fetchSettings();
      set({ settings, isLoading: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить настройки',
        isLoading: false,
      });
    }
  },
  save: async (body) => {
    set({ error: null });
    try {
      const settings = await updateSettings(body);
      set({ settings });
      return true;
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Не удалось сохранить настройки' });
      return false;
    }
  },
  saveToken: async (token) => {
    set({ error: null });
    try {
      const { tInvestTokenConfigured, tInvestTokenMasked } = await updateTInvestToken(token);
      set((state) => ({
        settings: state.settings && { ...state.settings, tInvestTokenConfigured, tInvestTokenMasked },
      }));
      return true;
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Не удалось сохранить токен' });
      return false;
    }
  },
}));
