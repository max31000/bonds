/**
 * true, если страница открыта внутри iframe portal-shell, false — standalone.
 * См. portal-shell/docs/service-routing-integration.md.
 */
export function useIsInsideShell(): boolean {
  try {
    return window.self !== window.top;
  } catch {
    return true; // не можем проверить (cross-origin) — считаем, что в iframe
  }
}
