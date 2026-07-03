import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { useLiveQuotes } from './useLiveQuotes';
import { useLiveStore } from '../store/useLiveStore';

/** Утилита — переключает document.visibilityState и стреляет событием, как это делает браузер. */
function setVisibility(state: DocumentVisibilityState) {
  Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('useLiveQuotes', () => {
  beforeEach(() => {
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
    setVisibility('visible');
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // Грубая проверка торговых часов на клиенте (Intl с явным timeZone) — фиксируем "сейчас" на
    // будний торговый час МСК, чтобы тест не зависел от реального времени запуска CI.
    vi.setSystemTime(new Date('2026-07-03T10:00:00Z')); // пятница, 13:00 МСК — внутри торгового окна
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('polls /api/live/positions immediately and writes the result into useLiveStore', async () => {
    server.use(
      http.get('*/api/live/positions', () =>
        HttpResponse.json({
          positions: [{ positionId: 1, instrumentId: 10, lastPriceRub: 1010, marketValueRub: 10100, changeDayPercent: 0.01, isStale: false, asOfUtc: '2026-07-03T10:00:00Z' }],
          totalMarketValueRub: 10100,
          asOfUtc: '2026-07-03T10:00:00Z',
        }),
      ),
    );

    renderHook(() => useLiveQuotes());

    await waitFor(() => expect(useLiveStore.getState().totalMarketValueRub).toBe(10100));
    expect(useLiveStore.getState().positionsById[1]?.marketValueRub).toBe(10100);
  });

  it('polls again after the interval elapses while the tab stays visible', async () => {
    let callCount = 0;
    server.use(
      http.get('*/api/live/positions', () => {
        callCount += 1;
        return HttpResponse.json({
          positions: [],
          totalMarketValueRub: callCount,
          asOfUtc: '2026-07-03T10:00:00Z',
        });
      }),
    );

    renderHook(() => useLiveQuotes());

    await waitFor(() => expect(callCount).toBe(1));

    await vi.advanceTimersByTimeAsync(60_000);
    await waitFor(() => expect(callCount).toBe(2));
  });

  it('stops polling when the tab becomes hidden', async () => {
    let callCount = 0;
    server.use(
      http.get('*/api/live/positions', () => {
        callCount += 1;
        return HttpResponse.json({ positions: [], totalMarketValueRub: 0, asOfUtc: '2026-07-03T10:00:00Z' });
      }),
    );

    renderHook(() => useLiveQuotes());
    await waitFor(() => expect(callCount).toBe(1));

    setVisibility('hidden');

    // Даже если интервал формально сработал бы — на скрытой вкладке поллинг не должен идти.
    await vi.advanceTimersByTimeAsync(120_000);
    expect(callCount).toBe(1);
  });

  it('resumes polling immediately when the tab becomes visible again', async () => {
    let callCount = 0;
    server.use(
      http.get('*/api/live/positions', () => {
        callCount += 1;
        return HttpResponse.json({ positions: [], totalMarketValueRub: 0, asOfUtc: '2026-07-03T10:00:00Z' });
      }),
    );

    renderHook(() => useLiveQuotes());
    await waitFor(() => expect(callCount).toBe(1));

    setVisibility('hidden');
    await vi.advanceTimersByTimeAsync(5_000);
    expect(callCount).toBe(1);

    setVisibility('visible');
    await waitFor(() => expect(callCount).toBe(2));
  });

  it('does not poll outside MOEX trading hours', async () => {
    vi.setSystemTime(new Date('2026-07-03T02:00:00Z')); // 05:00 МСК — вне торгового окна

    let callCount = 0;
    server.use(
      http.get('*/api/live/positions', () => {
        callCount += 1;
        return HttpResponse.json({ positions: [], totalMarketValueRub: 0, asOfUtc: '2026-07-03T02:00:00Z' });
      }),
    );

    renderHook(() => useLiveQuotes());

    await vi.advanceTimersByTimeAsync(1_000);
    expect(callCount).toBe(0);
  });
});
