/**
 * Грубая клиентская проверка "сейчас торговые часы MOEX" (plan/16 часть B) — не источник истины
 * (тот на бэкенде, LiveQuotesOptions), а фильтр, чтобы не спамить поллингом /api/live/positions
 * ночью и в выходные. Окно совпадает с бэкендовым дефолтом (LiveQuotesOptions: 09:50-19:00 МСК,
 * будни) — если бэкенд решит не отвечать данными вне своего окна, это не страшно (тик просто не
 * даст новых точек), но держать окна синхронными дешевле для UX (не мигать пустым поллингом).
 * <p>
 * Без dayjs-timezone плагина (в проекте не подключён) — используем Intl с явным timeZone,
 * поддерживается всеми целевыми браузерами без доп. зависимостей.
 */
export function isWithinMoexTradingHours(now: Date = new Date()): boolean {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: 'Europe/Moscow',
    hour: 'numeric',
    minute: 'numeric',
    weekday: 'short',
    hourCycle: 'h23',
  }).formatToParts(now);

  const get = (type: string) => parts.find((p) => p.type === type)?.value;
  const weekday = get('weekday');
  const hour = Number(get('hour'));
  const minute = Number(get('minute'));

  if (weekday === 'Sat' || weekday === 'Sun') return false;

  const minutesOfDay = hour * 60 + minute;
  const startMinutes = 9 * 60 + 50; // 09:50 MSK
  const endMinutes = 19 * 60; // 19:00 MSK

  return minutesOfDay >= startMinutes && minutesOfDay <= endMinutes;
}
