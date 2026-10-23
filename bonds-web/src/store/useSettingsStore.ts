import { create } from 'zustand';
import { fetchSettings, updateSettings } from '../api/settings';
import type { SettingsResponse, SettingsUpdateRequest } from '../api/types';

interface SettingsStore {
  settings: SettingsResponse | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  /**
   * Загружает настройки, только если они ещё не загружены и не грузятся — для компонентов, которым
   * настройки нужны «фоном» (горизонт анализа замены): десяток карточек не должны плодить десяток GET.
   */
  ensureLoaded: () => Promise<void>;
  save: (body: SettingsUpdateRequest) => Promise<boolean>;
}

/** Кэш настроек (GET/PUT /api/settings). Без `persist` — серверные данные, не клиентский UI-стейт. */
export const useSettingsStore = create<SettingsStore>()((set, get) => ({
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
  ensureLoaded: async () => {
    const { settings, isLoading } = get();
    if (settings || isLoading) return;
    await get().load();
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
}));

/**
 * Горизонт анализа замены из настроек пользователя, ГОДЫ (задача 48) — для тела POST /analytics/replacement.
 * undefined — настройки ещё не загружены: поле не отправляется и бэкенд сам подставляет настройку
 * (тот же источник), поэтому анализ не ломается и без загруженного стора. Фронтовых констант горизонта нет.
 */
export function getReplacementHorizonYears(): number | undefined {
  return useSettingsStore.getState().settings?.replacementHorizonYears;
}
