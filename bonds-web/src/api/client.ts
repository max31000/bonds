import { ApiError, apiJson, requestLogin } from '@mvv42/kit/platform';
import { useAccessStore } from '../store/useAccessStore';

// nginx проксирует /bonds/api/ → :5001/api/ (см. plan/01, часть C3). Относительный путь того же
// origin: общий mvv42-auth кладёт access-cookie на mvv42.ru, браузер сам отправит её с запросом.
const BASE_URL = import.meta.env.VITE_API_BASE ?? '/bonds/api';

/** Идентификатор сервиса в реестре shell / mvv42-auth. */
export const SERVICE_ID = 'bonds';

let loginRequested = false;

/**
 * JSON-запрос к API сервиса через `apiJson` кита: cookie, при 401 — один общий refresh сессии и
 * повтор. Финальный 401 → вход через shell (`requestLogin`), 403 (не владелец) → глобальный
 * экран «Нет доступа». Ошибка пробрасывается как `ApiError` (message = `error` из тела ответа),
 * сторы показывают её текст как раньше.
 */
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  try {
    return await apiJson<T>(`${BASE_URL}${path}`, init);
  } catch (err) {
    if (err instanceof ApiError) {
      if (err.status === 401 && !loginRequested) {
        loginRequested = true;
        requestLogin(SERVICE_ID);
      } else if (err.status === 403) {
        useAccessStore.getState().deny(err);
      }
    }
    throw err;
  }
}

function withBody(method: string, body?: unknown): RequestInit {
  return body === undefined ? { method } : { method, body: JSON.stringify(body) };
}

export const apiClient = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) => request<T>(path, withBody('POST', body)),
  put: <T>(path: string, body?: unknown) => request<T>(path, withBody('PUT', body)),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
};

/** Тесты: сбросить флаг «вход уже запрошен». */
export function __resetLoginRequestForTests(): void {
  loginRequested = false;
}
