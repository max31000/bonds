import type { ReliabilityLevel } from '../api/types';

/**
 * Задача 38 часть C.3 — значение UI-фильтра «не хуже уровня»: 'all' — без фильтра (дефолт, план
 * прямым текстом требует дефолт "все", не сужать список неожиданно для пользователя, который его
 * ещё не открывал — тот же принцип, что дефолт-выкл фильтра «похожая дюрация» задачи 37);
 * 'green' — только Green; 'yellow' — Green+Yellow (не хуже жёлтого). Зеркалит backend query-param
 * `reliability` (см. `UniverseQuery.reliability`) минус значение 'red' — «не хуже красного»
 * семантически равно «без фильтра», отдельная UI-опция не нужна (план перечисляет ровно 3 пункта:
 * 🟢/🟡/все). Вынесено из `ReliabilityFilterControl.tsx` в отдельный util-файл (не смешивать
 * экспорт компонента с экспортом функции/типа в одном файле — react-refresh/only-export-components).
 */
export type ReliabilityFilterValue = 'all' | 'green' | 'yellow';

/** Задача 38 часть C.3 — «не хуже уровня»: чистая функция, переиспользуется клиентским фильтром
 * блока 1 (панель кандидатов, фильтрует уже загруженный список без похода на бэкенд — тот же
 * приём, что фильтр «похожая дюрация» задачи 37). Скринер использует то же значение как СЕРВЕРНЫЙ
 * query-параметр (см. `useScreenerStore.load`) — эта функция ему не нужна, фильтрация там уже
 * сделал бэкенд (задача 38 часть B.2). */
export function meetsReliabilityFilter(level: ReliabilityLevel, filter: ReliabilityFilterValue): boolean {
  if (filter === 'all') return true;
  if (filter === 'green') return level === 'Green';
  return level === 'Green' || level === 'Yellow';
}
