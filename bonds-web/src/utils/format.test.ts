import { describe, it, expect } from 'vitest';
import {
  formatRub,
  daysUntil,
  formatDaysUntil,
  formatPercent,
  formatNumber,
  formatMonthLabel,
  formatDate,
  formatSharePercent,
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
  it('formats with 2 decimal digits', () => {
    expect(formatPercent(12.3456)).toBe('12.35%');
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
