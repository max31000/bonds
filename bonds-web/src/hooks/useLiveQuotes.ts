import { useEffect, useRef } from 'react';
import { fetchLivePositions } from '../api/live';
import { useLiveStore } from '../store/useLiveStore';
import { isWithinMoexTradingHours } from '../utils/tradingHours';

const POLL_INTERVAL_MS = 60_000;

/**
 * Поллит GET /api/live/positions раз в 60 сек (plan/16 часть B) — но ТОЛЬКО когда вкладка видима
 * (`document.visibilityState`) и грубо в торговые часы MOEX на клиенте (см. isWithinMoexTradingHours),
 * чтобы не спамить бэкенд ночью/на скрытой вкладке. Результат пишется в useLiveStore, откуда его
 * читает Positions.tsx (merge по positionId) и PortfolioIntradayChart (перерисовка после тика).
 * <p>
 * Осознанно НЕ использует SignalR/WebSocket — plan/16 прямо ограничивает контур поллингом.
 */
export function useLiveQuotes(): void {
  const setLivePositions = useLiveStore((s) => s.setLivePositions);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    const poll = async () => {
      if (document.visibilityState !== 'visible') return;
      if (!isWithinMoexTradingHours()) return;

      try {
        const response = await fetchLivePositions();
        setLivePositions(response.positions, response.totalMarketValueRub, response.asOfUtc);
      } catch {
        // Сетевой сбой одного тика поллинга не критичен — следующий тик попробует снова,
        // таблица позиций просто останется на предыдущих живых данных (не хуже, чем без live-контура).
      }
    };

    const startInterval = () => {
      if (intervalRef.current !== null) return;
      intervalRef.current = setInterval(poll, POLL_INTERVAL_MS);
    };

    const stopInterval = () => {
      if (intervalRef.current === null) return;
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    };

    const handleVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        // Вкладка снова видима — сразу опрашиваем (не ждать до 60 сек) и возобновляем интервал.
        void poll();
        startInterval();
      } else {
        stopInterval();
      }
    };

    if (document.visibilityState === 'visible') {
      void poll();
      startInterval();
    }

    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      stopInterval();
    };
  }, [setLivePositions]);
}
