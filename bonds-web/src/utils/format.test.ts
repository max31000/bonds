import { describe, it, expect } from 'vitest';
import {
  formatRub,
  formatRubCompact,
  formatRubCompactRange,
  daysUntil,
  formatDaysUntil,
  formatPercent,
  formatNumber,
  formatMonthLabel,
  formatDate,
  formatSharePercent,
  formatBp,
} from './format';

describe('formatRub', () => {
  it('formats a positive amount with RUB currency and no decimals', () => {
    const result = formatRub(1234567);
    expect(result).toContain('1');
    expect(result).toContain('234');
    expect(result).toContain('567');
    expect(result).toMatch(/₽/);
  });

  it('formats zero', () => {
    expect(formatRub(0)).toMatch(/0/);
  });

  it('returns a dash for null/undefined/NaN', () => {
    expect(formatRub(null)).toBe('—');
    expect(formatRub(undefined)).toBe('—');
    expect(formatRub(NaN)).toBe('—');
  });
});

describe('formatRubCompact', () => {
  it('formats millions with one decimal place', () => {
    expect(formatRubCompact(1_234_567)).toBe('1,2 млн ₽');
  });

  it('formats thousands with one decimal place', () => {
    expect(formatRubCompact(850_000)).toBe('850 тыс. ₽');
  });

  it('formats sub-thousand amounts as plain rubles', () => {
    expect(formatRubCompact(999)).toBe('999 ₽');
  });

  it('preserves the negative sign', () => {
    expect(formatRubCompact(-2_000_000)).toBe('-2 млн ₽');
  });

  it('returns a dash for null/undefined/NaN', () => {
    expect(formatRubCompact(null)).toBe('—');
    expect(formatRubCompact(undefined)).toBe('—');
    expect(formatRubCompact(NaN)).toBe('—');
  });
});

describe('formatRubCompactRange', () => {
  // Регрессия (задание, пункт 5): на узком диапазоне интрадей-графика (например, портфель
  // колеблется в пределах 15 480–15 520 ₽) formatRubCompact округляет обе границы до "15,5 тыс ₽" —
  // все подписи оси Y выглядят одинаковыми. formatRubCompactRange учитывает (max - min) и
  // переключается на более точный формат, когда компактное округление "схлопывает" диапазон.

  it('falls back to plain rubles when the range is narrow relative to the values', () => {
    const min = 15_480;
    const max = 15_520;
    expect(formatRubCompactRange(15_500, min, max)).toBe(formatRub(15_500));
    expect(formatRubCompactRange(15_480, min, max)).toBe(formatRub(15_480));
    expect(formatRubCompactRange(15_520, min, max)).toBe(formatRub(15_520));
    // И убеждаемся, что это действительно другой (более точный) формат, чем схлопывающий compact.
    expect(formatRubCompactRange(15_500, min, max)).not.toBe(formatRubCompact(15_500));
  });

  it('still distinguishes labels that would otherwise collide after compact rounding', () => {
    const min = 1_234_000;
    const max = 1_236_000;
    const lo = formatRubCompactRange(min, min, max);
    const hi = formatRubCompactRange(max, min, max);
    expect(lo).not.toBe(hi);
  });

  it('uses the normal compact format for a wide range', () => {
    const min = 1_000_000;
    const max = 5_000_000;
    expect(formatRubCompactRange(1_234_567, min, max)).toBe(formatRubCompact(1_234_567));
    expect(formatRubCompactRange(4_800_000, min, max)).toBe(formatRubCompact(4_800_000));
  });

  it('handles a flat/degenerate range (min === max) using the normal compact format', () => {
    expect(formatRubCompactRange(850_000, 850_000, 850_000)).toBe(formatRubCompact(850_000));
  });

  it('returns a dash for null/undefined/NaN value', () => {
    expect(formatRubCompactRange(null, 0, 100)).toBe('—');
    expect(formatRubCompactRange(undefined, 0, 100)).toBe('—');
    expect(formatRubCompactRange(NaN, 0, 100)).toBe('—');
  });
});

describe('daysUntil', () => {
  const now = new Date('2026-06-25T12:00:00Z');

  it('returns 0 for today', () => {
    expect(daysUntil('2026-06-25', now)).toBe(0);
  });

  it('returns a positive count for a future date', () => {
    expect(daysUntil('2030-01-01', now)).toBeGreaterThan(0);
  });

  it('returns the exact day count for a known future date', () => {
    expect(daysUntil('2026-07-05', now)).toBe(10);
  });

  it('returns a negative count for a past date', () => {
    expect(daysUntil('2020-01-01', now)).toBeLessThan(0);
  });

  it('returns null for invalid/missing input', () => {
    expect(daysUntil(null, now)).toBeNull();
    expect(daysUntil(undefined, now)).toBeNull();
    expect(daysUntil('not-a-date', now)).toBeNull();
  });
});

describe('formatDaysUntil', () => {
  const now = new Date('2026-06-25T12:00:00Z');

  it('formats today', () => {
    expect(formatDaysUntil('2026-06-25', now)).toBe('сегодня');
  });

  it('formats a future date', () => {
    expect(formatDaysUntil('2026-07-05', now)).toBe('через 10 дн.');
  });

  it('formats a past date', () => {
    expect(formatDaysUntil('2026-06-15', now)).toBe('10 дн. назад');
  });

  it('returns a dash for missing input', () => {
    expect(formatDaysUntil(null, now)).toBe('—');
  });
});

describe('formatPercent', () => {
  it('formats a fraction as a percentage with 2 decimal digits', () => {
    expect(formatPercent(0.1234)).toBe('12.34%');
  });

  it('formats zero', () => {
    expect(formatPercent(0)).toBe('0.00%');
  });

  it('returns a dash for null/undefined/NaN', () => {
    expect(formatPercent(null)).toBe('—');
    expect(formatPercent(undefined)).toBe('—');
    expect(formatPercent(NaN)).toBe('—');
  });
});

describe('formatNumber', () => {
  it('formats with default precision', () => {
    expect(formatNumber(3.14159)).toBe('3.14');
  });

  it('respects custom precision', () => {
    expect(formatNumber(3.14159, 3)).toBe('3.142');
  });

  it('returns a dash for null/undefined/NaN', () => {
    expect(formatNumber(null)).toBe('—');
    expect(formatNumber(undefined)).toBe('—');
    expect(formatNumber(NaN)).toBe('—');
  });
});

describe('formatMonthLabel', () => {
  it('formats a YYYY-MM string into a Russian month + year', () => {
    expect(formatMonthLabel('2026-07')).toBe('июль 2026');
    expect(formatMonthLabel('2026-01')).toBe('январь 2026');
    expect(formatMonthLabel('2026-12')).toBe('декабрь 2026');
  });

  it('returns the original string for malformed input', () => {
    expect(formatMonthLabel('not-a-month')).toBe('not-a-month');
  });

  it('returns a dash for missing input', () => {
    expect(formatMonthLabel(null)).toBe('—');
    expect(formatMonthLabel(undefined)).toBe('—');
  });
});

describe('formatDate', () => {
  it('formats an ISO date into DD.MM.YYYY', () => {
    expect(formatDate('2026-07-01')).toBe('01.07.2026');
  });

  it('returns a dash for missing/invalid input', () => {
    expect(formatDate(null)).toBe('—');
    expect(formatDate(undefined)).toBe('—');
    expect(formatDate('not-a-date')).toBe('—');
  });
});

describe('formatSharePercent', () => {
  it('formats with 1 decimal digit', () => {
    expect(formatSharePercent(12.345)).toBe('12.3%');
  });

  it('returns a dash for null/undefined/NaN', () => {
    expect(formatSharePercent(null)).toBe('—');
    expect(formatSharePercent(undefined)).toBe('—');
    expect(formatSharePercent(NaN)).toBe('—');
  });
});

describe('formatBp', () => {
  it('formats a fraction as basis points', () => {
    expect(formatBp(0.015)).toBe('150 б.п.');
  });

  it('formats G-spread correctly', () => {
    expect(formatBp(0.02)).toBe('200 б.п.');
  });

  it('returns a dash for null/undefined/NaN', () => {
    expect(formatBp(null)).toBe('—');
    expect(formatBp(undefined)).toBe('—');
    expect(formatBp(NaN)).toBe('—');
  });
});
