import { create } from 'zustand';
import { fetchSettings, updateSettings, updateTInvestToken } from '../api/settings';
import type { SettingsResponse, SettingsUpdateRequest } from '../api/types';

/** T-13/C: результат сохранения токена — успех несёт маску счёта-подтверждения, ошибка — сообщение. */
export type SaveTokenResult = { ok: true; validatedAccountIdMasked: string | null } | { ok: false; error: string };

interface SettingsStore {
  settings: SettingsResponse | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  save: (body: SettingsUpdateRequest) => Promise<boolean>;
  /** Токен никогда не хранится в сторе дольше момента отправки (см. план 09c §B.8). */
  saveToken: (token: string) => Promise<SaveTokenResult>;
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
      const { tInvestTokenConfigured, tInvestTokenMasked, validatedAccountIdMasked } =
        await updateTInvestToken(token);
      set((state) => ({
        settings: state.settings && { ...state.settings, tInvestTokenConfigured, tInvestTokenMasked },
      }));
      return { ok: true, validatedAccountIdMasked };
    } catch (err) {
      // 422 (plan/13 часть C: токен не прошёл проверку T-Invest) приходит тем же {error} телом,
      // что и прочие ошибки apiClient — сообщение уже человекочитаемое, эхо не содержит токен.
      const message = err instanceof Error ? err.message : 'Не удалось сохранить токен';
      set({ error: message });
      return { ok: false, error: message };
    }
  },
}));
