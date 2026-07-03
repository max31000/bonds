import { describe, it, expect } from 'vitest';
import { computePositionsTotals } from './positionsAggregation';
import type { PositionRow } from '../api/types';

function makePosition(overrides: Partial<PositionRow> = {}): PositionRow {
  return {
    positionId: 1,
    instrumentId: 10,
    name: 'Тестовая бумага',
    isin: 'RU000A1000A1',
    issuer: 'Тест-Эмитент',
    sector: 'Корп',
    quantity: 10,
    marketValueRub: 100_000,
    currencyRub: 'RUB',
    couponType: 'Fixed',
    maturityDate: '2030-01-01',
    horizonDate: '2030-01-01',
    calculatedToOffer: false,
    ytmEffective: 0.12,
    currentYield: 0.11,
    modifiedDuration: 3,
    gSpread: 0.005,
    isFloater: false,
    isIndexed: false,
    isEstimated: false,
    dataIncomplete: false,
    isOutOfScopeCurrency: false,
    averageCostRub: 9500,
    investedRub: 95_000,
    unrealizedPnlRub: 5_000,
    unrealizedPnlPercent: 0.0526,
    couponsReceivedRub: 1_000,
    totalReturnPercent: 0.0632,
    costBasisIncomplete: false,
    ...overrides,
  };
}

describe('computePositionsTotals', () => {
  it('returns zero/null totals for an empty portfolio', () => {
    const totals = computePositionsTotals([]);
    expect(totals.totalMarketValueRub).toBe(0);
    expect(totals.weightedYield).toBeNull();
    expect(totals.weightedDuration).toBeNull();
    expect(totals.hasExcludedFloaters).toBe(false);
  });

  it('sums market value across positions', () => {
    const totals = computePositionsTotals([
      makePosition({ marketValueRub: 100_000 }),
      makePosition({ positionId: 2, marketValueRub: 50_000 }),
    ]);
    expect(totals.totalMarketValueRub).toBe(150_000);
  });

  it('computes a market-value-weighted average yield across regular bonds', () => {
    // 100k at 10% + 300k at 20% => weighted = (100k*0.10 + 300k*0.20) / 400k = 0.175
    const totals = computePositionsTotals([
      makePosition({ positionId: 1, marketValueRub: 100_000, ytmEffective: 0.1 }),
      makePosition({ positionId: 2, marketValueRub: 300_000, ytmEffective: 0.2 }),
    ]);
    expect(totals.weightedYield).toBeCloseTo(0.175, 6);
  });

  it('excludes floater and indexed positions from the weighted yield and flags the footnote', () => {
    const totals = computePositionsTotals([
      makePosition({ positionId: 1, marketValueRub: 100_000, ytmEffective: 0.1 }),
      makePosition({ positionId: 2, marketValueRub: 900_000, isFloater: true, ytmEffective: null, currentYield: 0.5 }),
    ]);
    // Только первая бумага участвует — вес второй (флоатера) не искажает среднее.
    expect(totals.weightedYield).toBeCloseTo(0.1, 6);
    expect(totals.hasExcludedFloaters).toBe(true);
  });

  it('returns null weighted yield when every position is a floater/indexed', () => {
    const totals = computePositionsTotals([
      makePosition({ isFloater: true, ytmEffective: null }),
      makePosition({ positionId: 2, isIndexed: true, ytmEffective: null }),
    ]);
    expect(totals.weightedYield).toBeNull();
    expect(totals.hasExcludedFloaters).toBe(true);
  });

  it('computes a market-value-weighted average duration, including floaters (duration is always comparable)', () => {
    const totals = computePositionsTotals([
      makePosition({ positionId: 1, marketValueRub: 100_000, modifiedDuration: 2, isFloater: false }),
      makePosition({ positionId: 2, marketValueRub: 100_000, modifiedDuration: 4, isFloater: true, ytmEffective: null }),
    ]);
    expect(totals.weightedDuration).toBeCloseTo(3, 6);
  });

  it('skips positions with a null modifiedDuration when computing weighted duration', () => {
    const totals = computePositionsTotals([
      makePosition({ positionId: 1, marketValueRub: 100_000, modifiedDuration: null }),
      makePosition({ positionId: 2, marketValueRub: 100_000, modifiedDuration: 5 }),
    ]);
    expect(totals.weightedDuration).toBeCloseTo(5, 6);
  });

  it('does not flag the footnote when there are no floater/indexed positions', () => {
    const totals = computePositionsTotals([makePosition()]);
    expect(totals.hasExcludedFloaters).toBe(false);
  });
});
