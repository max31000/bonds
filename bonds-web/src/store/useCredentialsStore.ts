import { create } from 'zustand';
import { deleteCredential, fetchCapabilities, fetchCredentials, putCredential } from '../api/credentials';
import { fetchSettings } from '../api/settings';
import { useSettingsStore } from './useSettingsStore';
import type { Capabilities, CredentialRow } from '../api/types';

/** Результат сохранения секрета: ошибка несёт человекочитаемое сообщение бэкенда (422), без значения секрета. */
export type SaveCredentialResult = { ok: true } | { ok: false; error: string };

interface CredentialsStore {
  credentials: CredentialRow[];
  /** null — ещё не загружены. */
  capabilities: Capabilities | null;
  isLoading: boolean;
  error: string | null;
  load: () => Promise<void>;
  /** Загружает capabilities (один раз за сессию в ServiceLayout; повторно — после изменения секретов). */
  loadCapabilities: () => Promise<void>;
  /** Секрет — write-only: уходит в PUT и нигде в сторе не сохраняется. */
  save: (provider: string, fields: Record<string, string>) => Promise<SaveCredentialResult>;
  remove: (provider: string) => Promise<SaveCredentialResult>;
}

/** После смены токена T-Invest обновляем настройки «тихо» (без isLoading — иначе Settings размонтирует секции). */
async function refreshSettingsQuietly(provider: string): Promise<void> {
  if (provider !== 'tinvest_read') return;
  try {
    useSettingsStore.setState({ settings: await fetchSettings() });
  } catch {
    // best-effort: настройки обновятся при следующей загрузке страницы
  }
}

/** Кэш «Подключений» и capabilities (серверные данные, без persist). */
export const useCredentialsStore = create<CredentialsStore>()((set, get) => ({
  credentials: [],
  capabilities: null,
  isLoading: false,
  error: null,
  load: async () => {
    set({ isLoading: true, error: null });
    try {
      set({ credentials: await fetchCredentials(), isLoading: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить подключения', isLoading: false });
    }
  },
  loadCapabilities: async () => {
    try {
      set({ capabilities: await fetchCapabilities() });
    } catch {
      // capabilities необязательны для работы страниц — при ошибке остаются прежними (null = «неизвестно»)
    }
  },
  save: async (provider, fields) => {
    try {
      const row = await putCredential(provider, { fields });
      set((state) => ({ credentials: state.credentials.map((c) => (c.provider === provider ? row : c)) }));
      await Promise.all([get().loadCapabilities(), refreshSettingsQuietly(provider)]);
      return { ok: true };
    } catch (err) {
      return { ok: false, error: err instanceof Error ? err.message : 'Не удалось сохранить' };
    }
  },
  remove: async (provider) => {
    try {
      await deleteCredential(provider);
      await get().load();
      await Promise.all([get().loadCapabilities(), refreshSettingsQuietly(provider)]);
      return { ok: true };
    } catch (err) {
      return { ok: false, error: err instanceof Error ? err.message : 'Не удалось удалить' };
    }
  },
}));
