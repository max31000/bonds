/**
 * Значение UI-фильтра «не хуже уровня»: 'all' — без фильтра (дефолт, план
 * прямым текстом требует дефолт "все", не сужать список неожиданно для пользователя, который его
 * ещё не открывал — тот же принцип, что дефолт-выкл фильтра «похожая дюрация» задачи 37);
 * 'green' — только Green; 'yellow' — Green+Yellow (не хуже жёлтого). Зеркалит backend query-param
 * `reliability` (см. `UniverseQuery.reliability`) минус значение 'red' — «не хуже красного»
 * семантически равно «без фильтра», отдельная UI-опция не нужна (план перечисляет ровно 3 пункта:
 * 🟢/🟡/все). Вынесено из `ReliabilityFilterControl.tsx` в отдельный util-файл (не смешивать
 * экспорт компонента с экспортом функции/типа в одном файле — react-refresh/only-export-components).
 */
export type ReliabilityFilterValue = 'all' | 'green' | 'yellow';

/**
 * Значение UI-фильтра → серверный query-параметр `reliability` ('all' → без параметра). Блок 1 «Рекомендаций» (задача 50) и
 * скринер фильтруют НА СЕРВЕРЕ: клиентская фильтрация топ-20 могла показать 0 кандидатов, хотя дальше по рынку зелёные есть.
 */
export function toReliabilityQuery(filter: ReliabilityFilterValue): 'green' | 'yellow' | undefined {
  return filter === 'all' ? undefined : filter;
}
