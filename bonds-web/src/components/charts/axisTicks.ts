/**
 * Хелпер тиков оси дат/категорий (plan/18 часть A) — Recharts по умолчанию рисует подпись под
 * каждой точкой, что на графиках с 12+ месяцами превращается в нечитаемую кашу. Прореживаем до
 * ~8 подписей и подсказываем наклон, когда точек всё ещё много после прореживания.
 */
const MAX_TICKS = 8;

/**
 * Возвращает подмножество значений из `labels` для использования как `ticks` на `<XAxis>`,
 * равномерно распределённое так, чтобы итоговое количество подписей не превышало ~8, включая
 * последнюю точку.
 */
export function pickAxisTicks<T>(labels: T[], maxTicks = MAX_TICKS): T[] {
  if (labels.length <= maxTicks) return labels;

  const step = Math.ceil(labels.length / maxTicks);
  const picked: T[] = [];
  for (let i = 0; i < labels.length; i += step) {
    picked.push(labels[i]);
  }
  const last = labels[labels.length - 1];
  if (picked[picked.length - 1] !== last) picked.push(last);
  return picked;
}

/** Наклон подписей оси X, когда после прореживания подписей всё ещё тесно (>6 тиков). */
export function axisTickAngle(tickCount: number): number {
  return tickCount > 6 ? -30 : 0;
}
