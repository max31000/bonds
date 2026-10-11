import { useEffect } from 'react';
import { useLocation } from 'react-router-dom';

/**
 * Уведомляет portal-shell о навигации внутри сервиса через postMessage.
 * Не отправляет сообщения в standalone-режиме (window.self === window.top).
 *
 * Shell и сервисы живут на одном origin (shell на `/`, сервис на `/bonds/`), поэтому
 * targetOrigin = window.location.origin — без хардкода домена.
 * Протокол сообщений: mvv42-infra/docs/specs/2026-10-11-platform-design.md.
 */
export function useShellSync(serviceId: string) {
  const location = useLocation();

  useEffect(() => {
    if (window.self === window.top) return;

    window.parent.postMessage(
      {
        type: 'NAVIGATE',
        serviceId,
        path: location.pathname + location.search + location.hash,
      },
      window.location.origin,
    );
  }, [location, serviceId]);
}
