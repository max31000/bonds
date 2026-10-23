import { describe, expect, it } from 'vitest';
import {
  barGeometry,
  formatDrift,
  formatPortfolioMoney,
  formatWeight,
  fractionToPercent,
  percentToFraction,
  targetsSumOk,
} from './baskets';

describe('конверсия доля ↔ проценты (ровно один раз на границе формы)', () => {
  it('проценты поля ввода → доля API без артефактов плавающей точки', () => {
    expect(percentToFraction(15)).toBe(0.15);
    expect(percentToFraction(7)).toBe(0.07);
    expect(percentToFraction(0.5)).toBe(0.005);
    expect(percentToFraction(33.3333)).toBe(0.333333);
    expect(percentToFraction(0)).toBe(0);
  });

  it('доля API → проценты поля ввода', () => {
    expect(fractionToPercent(0.15)).toBe(15);
    expect(fractionToPercent(0.07)).toBe(7);
    expect(fractionToPercent(0.333333)).toBe(33.3333);
  });

  it('туда-обратно возвращает исходное значение', () => {
    for (const f of [0, 0.02, 0.05, 0.15, 0.6, 0.85, 1]) {
      expect(percentToFraction(fractionToPercent(f))).toBe(f);
    }
  });
});

describe('форматтеры', () => {
  it('formatWeight рисует долю процентами', () => {
    expect(formatWeight(0.12)).toBe('12,0%');
    expect(formatWeight(0.0925)).toBe('9,3%');
    expect(formatWeight(null)).toBe('—');
  });

  it('formatDrift — знак и процентные пункты', () => {
    expect(formatDrift(-0.03)).toBe('−3,0 п.п.');
    expect(formatDrift(0.015)).toBe('+1,5 п.п.');
    expect(formatDrift(0)).toBe('0,0 п.п.');
    expect(formatDrift(null)).toBe('—');
  });

  it('formatPortfolioMoney: рубли со знаком ₽, доллары с кодом валюты', () => {
    expect(formatPortfolioMoney(30000, 'RUB')).toMatch(/30\s000\s₽/);
    expect(formatPortfolioMoney(1674, 'USD')).toMatch(/USD/);
  });
});

describe('targetsSumOk', () => {
  it('сумма 100% в допуске 0.0001; null не считаются; пусто — проверять нечего', () => {
    expect(targetsSumOk([0.15, 0.85, null])).toBe(true);
    expect(targetsSumOk([0.15, 0.8501])).toBe(true);
    expect(targetsSumOk([0.2, 0.85])).toBe(false);
    expect(targetsSumOk([null, null])).toBe(true);
  });
});

describe('barGeometry', () => {
  it('факт, цель и допуск помещаются в трек 0..1', () => {
    const g = barGeometry({ weight: 0.12, target: 0.15, band: 0.02, hardBand: 0.05 });
    expect(g.target).not.toBeNull();
    for (const v of [g.actual, g.target as number, g.bandFrom, g.bandTo, g.hardFrom, g.hardTo]) {
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThanOrEqual(1);
    }
    expect(g.bandFrom).toBeLessThan(g.target as number);
    expect(g.bandTo).toBeGreaterThan(g.target as number);
    expect(g.actual).toBeLessThan(g.target as number);
  });

  it('факт больше цели: масштаб растёт, факт не обрезается', () => {
    const g = barGeometry({ weight: 0.9, target: 0.15, band: 0.02, hardBand: 0.05 });
    expect(g.actual).toBeGreaterThan(0.85);
    expect(g.actual).toBeLessThanOrEqual(1);
  });

  it('без цели цели на полосе нет', () => {
    expect(barGeometry({ weight: 0.2, target: null, band: null, hardBand: null }).target).toBeNull();
  });
});
