import { describe, it, expect } from 'vitest';
import { buildScatterChartData, pointCategory, CATEGORY_COLOR } from './scatterChartData';
import type { ScatterPoint } from '../api/types';

function makePoint(overrides: Partial<ScatterPoint> = {}): ScatterPoint {
  return {
    positionId: 1,
    instrumentId: 10,
    name: 'Тестовая бумага',
    issuer: 'Тест-Эмитент',
    modifiedDuration: 2,
    macaulayDuration: 2.1,
    effectiveYield: 0.12,
    yieldKind: 'Ytm',
    isFloater: false,
    isIndexed: false,
    isEstimated: false,
    dataIncomplete: false,
    isWatchlist: false,
    ...overrides,
  };
}

describe('pointCategory', () => {
  it('categorizes a watchlist point as "Watchlist" — takes priority over other flags', () => {
    // Задача 20: watchlist-точка должна попадать в свою категорию, даже если она одновременно
    // floater/dataIncomplete — иначе она смешалась бы с категориями своего портфеля на графике.
    const point = makePoint({ isWatchlist: true, isFloater: true, dataIncomplete: true });
    expect(pointCategory(point)).toBe('Watchlist');
  });

  it('categorizes a regular portfolio point as "Обычная"', () => {
    expect(pointCategory(makePoint())).toBe('Обычная');
  });

  it('categorizes a floater portfolio point as "Флоатер"', () => {
    expect(pointCategory(makePoint({ isFloater: true }))).toBe('Флоатер');
  });

  it('has a distinct color for the Watchlist category from all portfolio categories', () => {
    const watchlistColor = CATEGORY_COLOR['Watchlist'];
    expect(watchlistColor).toBeTruthy();
    expect(Object.entries(CATEGORY_COLOR).filter(([key]) => key !== 'Watchlist').map(([, v]) => v)).not.toContain(
      watchlistColor,
    );
  });
});

describe('buildScatterChartData with watchlist points', () => {
  it('includes watchlist points alongside portfolio points, preserving isWatchlist', () => {
    const portfolioPoint = makePoint({ positionId: 1, instrumentId: 10 });
    const watchlistPoint = makePoint({ positionId: 0, instrumentId: 20, isWatchlist: true, macaulayDuration: 6 });

    const { points } = buildScatterChartData({
      points: [portfolioPoint, watchlistPoint],
      curve: [],
    });

    expect(points).toHaveLength(2);
    expect(points.find((p) => p.instrumentId === 20)?.isWatchlist).toBe(true);
    expect(points.find((p) => p.instrumentId === 10)?.isWatchlist).toBe(false);
  });

  it('extends the X-axis domain to cover watchlist points beyond the portfolio range', () => {
    const portfolioPoint = makePoint({ macaulayDuration: 2 });
    const watchlistPoint = makePoint({ instrumentId: 20, isWatchlist: true, macaulayDuration: 15 });

    const { xDomainMax } = buildScatterChartData({
      points: [portfolioPoint, watchlistPoint],
      curve: [],
    });

    expect(xDomainMax).toBeGreaterThanOrEqual(15);
  });
});
