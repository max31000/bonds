import { IconEye } from '@tabler/icons-react';

/** Текст баннера режима наблюдения (фаза 2: заявки брокерам не отправляются нигде и никак). */
export const DRY_RUN_BANNER_TEXT = 'Режим наблюдения: сделки не отправляются';

/** Постоянный баннер страницы «Планы»: планы — только данные, ничего не уходит брокеру. */
export function DryRunBanner() {
  return (
    <aside className="bonds-dryrun-banner" role="status" data-testid="dryrun-banner">
      <IconEye size={16} stroke={1.8} aria-hidden="true" />
      <span>{DRY_RUN_BANNER_TEXT}</span>
    </aside>
  );
}
