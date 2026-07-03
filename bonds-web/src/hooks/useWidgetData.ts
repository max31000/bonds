import { useEffect, useState } from 'react';

interface WidgetDataState<T> {
  data: T | null;
  isLoading: boolean;
  error: string | null;
}

/**
 * Загружает данные одного виджета дашборда независимо от остальных (plan/18 часть B.5) —
 * каждая карточка дашборда дёргает свой эндпоинт напрямую (не через "экранные" сторы вроде
 * useAnalyticsStore, где Promise.all роняет `error` для всех виджетов сразу при отказе одного
 * запроса). Отказ одного эндпоинта помечает ошибкой только эту карточку, остальные не задеты.
 */
export function useWidgetData<T>(fetcher: () => Promise<T>, deps: unknown[] = []): WidgetDataState<T> {
  const [state, setState] = useState<WidgetDataState<T>>({ data: null, isLoading: true, error: null });

  useEffect(() => {
    let cancelled = false;
    setState((prev) => ({ ...prev, isLoading: true, error: null }));

    fetcher()
      .then((data) => {
        if (!cancelled) setState({ data, isLoading: false, error: null });
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setState({
            data: null,
            isLoading: false,
            error: err instanceof Error ? err.message : 'Не удалось загрузить данные',
          });
        }
      });

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return state;
}
