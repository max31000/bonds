import { useAuthStore } from '../store/useAuthStore';

// nginx проксирует /bonds/api/ → :5001/api/ (см. plan/01, часть C3).
// Фронт ходит на относительный путь, поэтому дефолт работает и standalone, и в iframe.
const BASE_URL = import.meta.env.VITE_API_BASE ?? '/bonds/api';

function getAuthHeaders(): Record<string, string> {
  const token = useAuthStore.getState().token;
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE_URL}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...getAuthHeaders(),
      ...(options?.headers ?? {}),
    },
  });

  if (res.status === 401) {
    // Токен истёк или невалиден — разлогиниваем и уводим на экран входа
    useAuthStore.getState().logout();
    window.location.href = `${import.meta.env.BASE_URL ?? '/'}login`;
    throw new Error('Требуется авторизация');
  }

  if (!res.ok) {
    const error = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error((error as { error?: string }).error ?? `HTTP ${res.status}`);
  }

  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}

export const apiClient = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: 'POST',
      body: body ? JSON.stringify(body) : undefined,
    }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: 'PUT',
      body: body ? JSON.stringify(body) : undefined,
    }),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
};
