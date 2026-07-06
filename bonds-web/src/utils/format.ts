/**
 * Форматтеры, используемые экраном таблицы позиций (этап 09a).
 * Форматтеры для календаря/аналитики (09b) — добавляются отдельно в `utils/`.
 */

/** Форматирует сумму в рублях с разделителями тысяч и без копеек (например, "1 234 567 ₽"). */
export function formatRub(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    maximumFractionDigits: 0,
  }).format(value);
}

/**
 * Компактный формат рублёвой суммы для тесных мест (ось графика, plan/15 §C.1) — "1,2 млн ₽",
 * "850 тыс. ₽", "999 ₽". Округляет до 1 знака после запятой у млн/тыс. (без хвоста ",0").
 */
export function formatRubCompact(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  const sign = value < 0 ? '-' : '';
  const abs = Math.abs(value);

  const round1 = (n: number) => Math.round(n * 10) / 10;

  if (abs >= 1_000_000) {
    return `${sign}${round1(abs / 1_000_000).toLocaleString('ru-RU')} млн ₽`;
  }
  if (abs >= 1_000) {
    return `${sign}${round1(abs / 1_000).toLocaleString('ru-RU')} тыс. ₽`;
  }
  return `${sign}${Math.round(abs).toLocaleString('ru-RU')} ₽`;
}

/**
 * Range-aware вариант {@link formatRubCompact} для подписей оси Y узких временных рядов (plan/16
 * интрадей-график портфеля). На узком диапазоне (например, портфель колеблется в пределах пары
 * тысяч рублей на фоне абсолютной величины в сотни тысяч) обычное округление до "1 знак после
 * запятой у тыс./млн" схлопывает все деления оси в одну и ту же подпись ("15,5 тыс ₽" четыре раза
 * подряд) — визуально нечитаемо. Если (max - min) достаточно мал относительно масштаба значений,
 * показываем точные рубли без компактного округления; иначе — обычный компактный формат.
 */
export function formatRubCompactRange(
  value: number | null | undefined,
  min: number,
  max: number,
): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';

  const range = Math.abs(max - min);
  const scale = Math.max(Math.abs(max), Math.abs(min), 1);

  // Порог: если диапазон меньше ~2% от масштаба значений, компактное округление (1 знак после
  // запятой у тыс./млн) даёт точность хуже, чем сам диапазон — переключаемся на целые рубли.
  const isNarrowRange = range > 0 && range / scale < 0.02;

  if (isNarrowRange) {
    return formatRub(value);
  }

  return formatRubCompact(value);
}

/**
 * Считает целое число календарных дней от сегодня (UTC-полночь) до `dateIso` (включительно).
 * Возвращает null, если дата не распознана.
 * Отрицательное значение — дата уже прошла (используется для дат в прошлом, теоретически
 * не должно происходить для горизонта позиции, но обрабатываем на случай рассинхрона данных).
 */
export function daysUntil(dateIso: string | null | undefined, now: Date = new Date()): number | null {
  if (!dateIso) return null;
  const target = new Date(dateIso);
  if (Number.isNaN(target.getTime())) return null;

  const startOfToday = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const startOfTarget = Date.UTC(target.getUTCFullYear(), target.getUTCMonth(), target.getUTCDate());

  const msPerDay = 24 * 60 * 60 * 1000;
  return Math.round((startOfTarget - startOfToday) / msPerDay);
}

/** Форматирует число дней до даты в человекочитаемую строку ("через 12 дн.", "сегодня", "3 дн. назад"). */
export function formatDaysUntil(dateIso: string | null | undefined, now: Date = new Date()): string {
  const days = daysUntil(dateIso, now);
  if (days === null) return '—';
  if (days === 0) return 'сегодня';
  if (days > 0) return `через ${days} дн.`;
  return `${Math.abs(days)} дн. назад`;
}

/** Форматирует процентное значение (например, доходность) с 2 знаками после запятой. Ожидает дробь (0-1), умножает на 100. */
export function formatPercent(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${(value * 100).toFixed(2)}%`;
}

/** Форматирует значение в базисные пункты (б.п.). Ожидает дробь (0-1), умножает на 10000. */
export function formatBp(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${Math.round(value * 10000)} б.п.`;
}

/** Форматирует число (например, дюрацию в годах) с заданной точностью, по умолчанию 2 знака. */
export function formatNumber(value: number | null | undefined, fractionDigits = 2): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return value.toFixed(fractionDigits);
}

/**
 * Человекочитаемый маппинг источника эффективной ставки комиссии (plan/22 части D/E) —
 * переиспользуется экраном настроек и подписями на карточках замен/аллокации/«если продать сейчас».
 */
export function commissionSourceLabel(source: 'UserOverride' | 'EstimatedFromTrades' | 'Default' | 'ExplicitRequest'): string {
  switch (source) {
    case 'UserOverride':
      return 'ваш override в настройках';
    case 'EstimatedFromTrades':
      return 'из ваших сделок';
    case 'ExplicitRequest':
      return 'указана явно';
    case 'Default':
    default:
      return 'дефолт 0.3%';
  }
}

/** Форматирует месяц вида "2026-07" в человекочитаемый "июль 2026". */
export function formatMonthLabel(month: string | null | undefined): string {
  if (!month) return '—';
  const match = /^(\d{4})-(\d{2})$/.exec(month);
  if (!match) return month;
  const [, year, monthNum] = match;
  const monthNames = [
    'январь',
    'февраль',
    'март',
    'апрель',
    'май',
    'июнь',
    'июль',
    'август',
    'сентябрь',
    'октябрь',
    'ноябрь',
    'декабрь',
  ];
  const idx = Number(monthNum) - 1;
  if (idx < 0 || idx > 11) return month;
  return `${monthNames[idx]} ${year}`;
}

/** Форматирует дату ISO ("2026-07-01") в краткий человекочитаемый вид ("01.07.2026"). */
export function formatDate(dateIso: string | null | undefined): string {
  if (!dateIso) return '—';
  const date = new Date(dateIso);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(
    date,
  );
}

/** Форматирует долю в процентах (например, состав портфеля), 1 знак после запятой. */
export function formatSharePercent(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${value.toFixed(1)}%`;
}

/** Форматирует дату-время ISO (UTC) в краткий локальный вид ("01.07.2026, 14:30"). */
export function formatDateTime(dateIso: string | null | undefined): string {
  if (!dateIso) return '—';
  const date = new Date(dateIso);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}
