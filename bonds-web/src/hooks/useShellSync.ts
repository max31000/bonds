import { useEffect } from 'react';
import { useLocation } from 'react-router-dom';

const SHELL_ORIGIN = 'https://mvv42.ru';

/**
 * Уведомляет portal-shell о навигации внутри сервиса через postMessage.
 * Не отправляет сообщения в standalone-режиме (window.self === window.top).
 * См. portal-shell/docs/service-routing-integration.md.
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
      SHELL_ORIGIN,
    );
  }, [location, serviceId]);
}
