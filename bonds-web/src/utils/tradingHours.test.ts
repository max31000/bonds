import { describe, it, expect } from 'vitest';
import { isWithinMoexTradingHours } from './tradingHours';

describe('isWithinMoexTradingHours', () => {
  it('returns true for a weekday within the trading window', () => {
    // Пятница, 13:00 МСК (10:00 UTC).
    expect(isWithinMoexTradingHours(new Date('2026-07-03T10:00:00Z'))).toBe(true);
  });

  it('returns false before the window opens', () => {
    // Пятница, 09:00 МСК (06:00 UTC) — до открытия торгов.
    expect(isWithinMoexTradingHours(new Date('2026-07-03T06:00:00Z'))).toBe(false);
  });

  it('returns false after the window closes', () => {
    // Пятница, 20:00 МСК (17:00 UTC) — после закрытия окна.
    expect(isWithinMoexTradingHours(new Date('2026-07-03T17:00:00Z'))).toBe(false);
  });

  it('returns false on Saturday even during trading hours', () => {
    // Суббота, 13:00 МСК.
    expect(isWithinMoexTradingHours(new Date('2026-07-04T10:00:00Z'))).toBe(false);
  });

  it('returns false on Sunday even during trading hours', () => {
    // Воскресенье, 13:00 МСК.
    expect(isWithinMoexTradingHours(new Date('2026-07-05T10:00:00Z'))).toBe(false);
  });

  it('returns true exactly at the window boundaries', () => {
    // 09:50 МСК ровно (06:50 UTC) — начало окна.
    expect(isWithinMoexTradingHours(new Date('2026-07-03T06:50:00Z'))).toBe(true);
    // 19:00 МСК ровно (16:00 UTC) — конец окна.
    expect(isWithinMoexTradingHours(new Date('2026-07-03T16:00:00Z'))).toBe(true);
  });
});
