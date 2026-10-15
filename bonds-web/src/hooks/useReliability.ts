import { useEffect, useState } from 'react';
import { fetchReliability } from '../api/universe';
import type { ReliabilityResponse } from '../api/types';

interface ReliabilityState {
  data: ReliabilityResponse | null;
  isLoading: boolean;
  error: string | null;
}

/**
 * Загружает полную карточку скоркарты бумаги (GET /api/universe/{secid|isin}/reliability). `refreshToken`
 * увеличивается после ручного ввода рейтинга/отчётности — карточка перезапрашивается (бэкенд к этому моменту уже
 * сбросил кэш скоркарты). `lookup` null — не грузим (карточка уже есть у родителя).
 */
export function useReliability(lookup: string | null, refreshToken = 0): ReliabilityState {
  const [state, setState] = useState<ReliabilityState>({ data: null, isLoading: lookup !== null, error: null });

  useEffect(() => {
    if (lookup === null) return;
    let cancelled = false;

    // Старые данные не сбрасываем при рефетче (карточка не мигает); ошибка — только для текущего запроса.
    fetchReliability(lookup)
      .then((data) => {
        if (!cancelled) setState({ data, isLoading: false, error: null });
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setState({
            data: null,
            isLoading: false,
            error: err instanceof Error ? err.message : 'Не удалось загрузить скоркарту',
          });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [lookup, refreshToken]);

  return state;
}
