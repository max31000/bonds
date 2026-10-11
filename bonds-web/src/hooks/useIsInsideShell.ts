/**
 * true, если страница открыта внутри iframe portal-shell, false — standalone.
 * См. mvv42-infra/docs/specs/2026-10-11-platform-design.md.
 */
export function useIsInsideShell(): boolean {
  try {
    return window.self !== window.top;
  } catch {
    return true; // не можем проверить (cross-origin) — считаем, что в iframe
  }
}
