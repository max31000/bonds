import { create } from 'zustand';
import { fetchRelativeValue } from '../api/recommendations';
import type { RelativeValuePosition } from '../api/types';

interface RelativeValueStore {
  /** По positionId — последняя загруженная RV-оценка (GET /api/analytics/relative-value). */
  positionsById: Record<number, RelativeValuePosition>;
  disclaimer: string;
  isLoading: boolean;
  /** Не null — только для диагностики/тестов; UI НЕ показывает это как блокирующую ошибку
   * (план часть D: отказ эндпоинта не должен ронять страницу рекомендаций/таблицу позиций —
   * индикатор просто не показывается, см. Positions.tsx/Recommendations.tsx). */
  error: string | null;
  hasLoaded: boolean;
  load: () => Promise<void>;
}

/**
 * Задача 30 часть D — relative value (RV) держится в ОТДЕЛЬНОМ сторе от useRecommendationsStore/
 * usePositionsStore (тот же принцип, что useLiveStore для живых котировок): один bulk-запрос
 * GET /api/analytics/relative-value, результат кладётся в словарь по positionId, любой компонент
 * (бейджи на слабых звеньях, секция «дорогие/дешёвые», компактный индикатор в таблице позиций)
 * читает свой срез без повторных запросов. Ошибка эндпоинта ловится здесь и НЕ прокидывается
 * дальше как исключение — потребители проверяют error/hasLoaded и просто не показывают
 * RV-индикатор, не блокируя остальной UI (план явно требует устойчивость к отказу).
 */
export const useRelativeValueStore = create<RelativeValueStore>()((set, get) => ({
  positionsById: {},
  disclaimer: '',
  isLoading: false,
  error: null,
  hasLoaded: false,

  load: async () => {
    if (get().isLoading || get().hasLoaded) return; // один запрос на сессию страницы — тот же принцип, что ленивая загрузка индикатора в таблице.
    set({ isLoading: true, error: null });
    try {
      const response = await fetchRelativeValue();
      set({
        positionsById: Object.fromEntries(response.positions.map((p) => [p.positionId, p])),
        disclaimer: response.disclaimer,
        isLoading: false,
        hasLoaded: true,
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Не удалось загрузить relative value',
        isLoading: false,
        hasLoaded: true,
      });
    }
  },
}));
