import { describe, it, expect } from 'vitest';
import { pickAxisTicks, axisTickAngle } from './axisTicks';

describe('pickAxisTicks', () => {
  it('returns all labels unchanged when there are fewer than the max', () => {
    const labels = ['a', 'b', 'c'];
    expect(pickAxisTicks(labels)).toEqual(labels);
  });

  it('returns all labels unchanged when exactly at the max', () => {
    const labels = Array.from({ length: 8 }, (_, i) => `m${i}`);
    expect(pickAxisTicks(labels)).toEqual(labels);
  });

  it('thins out labels to at most ~8 when there are many more', () => {
    const labels = Array.from({ length: 24 }, (_, i) => `m${i}`);
    const picked = pickAxisTicks(labels);
    expect(picked.length).toBeLessThanOrEqual(9);
    expect(picked.length).toBeGreaterThan(1);
  });

  it('always includes the last label', () => {
    const labels = Array.from({ length: 24 }, (_, i) => `m${i}`);
    const picked = pickAxisTicks(labels);
    expect(picked[picked.length - 1]).toBe('m23');
  });

  it('respects a custom maxTicks', () => {
    const labels = Array.from({ length: 10 }, (_, i) => `m${i}`);
    const picked = pickAxisTicks(labels, 4);
    expect(picked.length).toBeLessThanOrEqual(5);
  });
});

describe('axisTickAngle', () => {
  it('returns 0 when there is room (<=6 ticks)', () => {
    expect(axisTickAngle(3)).toBe(0);
    expect(axisTickAngle(6)).toBe(0);
  });

  it('returns a negative angle when ticks are tight (>6)', () => {
    expect(axisTickAngle(8)).toBe(-30);
  });
});
