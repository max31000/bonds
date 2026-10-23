import { describe, expect, it } from 'vitest';
import { baseIncome, closedBond, fundPosition, openBond } from '../test/incomeFixtures';
import { buildBreakdown, ledgerKindLabel, parsePeriod, parseShowClosed, periodRange, realizedByKindHint, sortPositions } from './incomeDisplay';

describe('incomeDisplay', () => {
  it('realizedByKindHint: курсовая разница валюты видна отдельно от облигаций; один класс или неполный список — null', () => {
    const fmt = (n: number | null | undefined) => `${n} ₽`;
    const currency = { ...closedBond, assetKind: 'currency' as const, assetId: 99, realizedPnlRub: 12207 };
    const fund = { ...fundPosition, realizedPnlRub: -7100 };
    expect(realizedByKindHint([closedBond, currency, fund], false, 1500 + 12207 - 7100, fmt)).toBe(
      'облигации +1500 ₽ · фонды -7100 ₽ · валюта +12207 ₽',
    );
    expect(realizedByKindHint([closedBond, openBond], false, 1500, fmt)).toBeNull();
    expect(realizedByKindHint([closedBond, currency], false, 99999, fmt)).toBeNull();
  });

  it('parsePeriod: допустимые значения и откат на «всё время»', () => {
    expect(parsePeriod('ytd')).toBe('ytd');
    expect(parsePeriod('12m')).toBe('12m');
    expect(parsePeriod('2025')).toBe('2025');
    expect(parsePeriod('abc')).toBe('all');
    expect(parsePeriod(null)).toBe('all');
  });

  it('parseShowClosed: по умолчанию показываем, closed=0 скрывает', () => {
    expect(parseShowClosed(null)).toBe(true);
    expect(parseShowClosed('1')).toBe(true);
    expect(parseShowClosed('0')).toBe(false);
  });

  it('periodRange: границы относительно переданной «сегодня»', () => {
    const today = new Date(2026, 9, 21); // 21.10.2026 локально
    expect(periodRange('all', today)).toEqual({});
    expect(periodRange('ytd', today)).toEqual({ from: '2026-01-01', to: '2026-10-21' });
    expect(periodRange('12m', today)).toEqual({ from: '2025-10-22', to: '2026-10-21' });
    expect(periodRange('2025', today)).toEqual({ from: '2025-01-01', to: '2025-12-31' });
    expect(periodRange('2026', today)).toEqual({ from: '2026-01-01', to: '2026-10-21' });
  });

  it('ledgerKindLabel: income у облигации — Купон, у фонда — Выплата фонда', () => {
    expect(ledgerKindLabel('income', 'bond')).toBe('Купон');
    expect(ledgerKindLabel('income', 'fund')).toBe('Выплата фонда');
    expect(ledgerKindLabel('principalReturn', 'bond')).toBe('Амортизация');
    expect(ledgerKindLabel('redemption', 'bond')).toBe('Погашение');
  });

  it('buildBreakdown: компоненты в сумме дают итог (инвариант бэкенда)', () => {
    const items = buildBreakdown(baseIncome.totals, baseIncome.accountLevel);
    const total = items.find((i) => i.isTotal)!;
    const sum = items.filter((i) => !i.isTotal).reduce((s, i) => s + i.value, 0);
    // брутто − НКД уплач. + получ. + набежал + налоги/комиссии (с «по счёту») + продажи + бумажная = итог
    expect(sum).toBeCloseTo(total.value, 5);
    expect(items[items.length - 1].isTotal).toBe(true);
    expect(items.find((i) => i.key === 'accruedPaid')!.value).toBe(-1800);
    expect(items.find((i) => i.key === 'taxes')!.value).toBeCloseTo(-2568.8, 5);
  });

  it('sortPositions: null в конце при любом направлении', () => {
    const rows = [closedBond, openBond, fundPosition];
    const asc = sortPositions(rows, { key: 'unrealized', dir: 'asc' }, false).map((p) => p.assetId);
    const desc = sortPositions(rows, { key: 'unrealized', dir: 'desc' }, false).map((p) => p.assetId);
    expect(asc[asc.length - 1]).toBe(2);
    expect(desc[desc.length - 1]).toBe(2);
  });
});
