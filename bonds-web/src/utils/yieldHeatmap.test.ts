import { describe, it, expect } from 'vitest';
import { buildYieldHeatmapScale } from './yieldHeatmap';

describe('buildYieldHeatmapScale', () => {
  it('returns undefined for every value when given an empty array', () => {
    const scale = buildYieldHeatmapScale([]);
    expect(scale(0.1)).toBeUndefined();
  });

  it('assigns a higher alpha (more saturated color-mix percentage) to higher percentile values', () => {
    const scale = buildYieldHeatmapScale([0.05, 0.1, 0.15, 0.2]);
    const low = scale(0.05);
    const high = scale(0.2);
    expect(low).toBeDefined();
    expect(high).toBeDefined();

    const extractPercent = (css: string | undefined) => Number(/(\d+)%/.exec(css ?? '')?.[1] ?? NaN);
    expect(extractPercent(high)).toBeGreaterThan(extractPercent(low));
  });

  it('returns a color-mix CSS value referencing the violet theme variable', () => {
    const scale = buildYieldHeatmapScale([0.1, 0.2]);
    const result = scale(0.2);
    expect(result).toContain('color-mix');
    expect(result).toContain('var(--mantine-color-violet-6)');
  });

  it('gives equal values the same color regardless of position in the array', () => {
    const scale = buildYieldHeatmapScale([0.1, 0.1, 0.1]);
    expect(scale(0.1)).toBe(scale(0.1));
  });

  it('returns undefined for a non-finite value', () => {
    const scale = buildYieldHeatmapScale([0.1, 0.2]);
    expect(scale(NaN)).toBeUndefined();
  });
});
