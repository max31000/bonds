import { useState } from 'react';
import { Button } from '@mantine/core';
import { IconBolt, IconEye } from '@tabler/icons-react';

/** Текст баннера режима наблюдения: заявки брокерам не отправляются. */
export const DRY_RUN_BANNER_TEXT = 'Режим наблюдения: сделки не отправляются';

/** Текст баннера живого режима (задача 65): все ворота открыты, подтверждённые планы уходят брокеру. */
export const LIVE_BANNER_TEXT = 'Исполнение включено: подтверждённые планы отправляются брокеру T-Invest';

/**
 * Баннер страницы «Планы». `live` берётся из ответа сервера (`dryRun === false` — все ворота исполнения открыты); пока данных нет — режим наблюдения
 * (безопасный дефолт). В живом режиме — кнопка «Остановить» (= `/stop` в боте: пауза, висящие заявки снимаются).
 */
export function DryRunBanner({ live = false, onStop }: { live?: boolean; onStop?: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  if (!live) {
    return (
      <aside className="bonds-dryrun-banner" role="status" data-testid="dryrun-banner">
        <IconEye size={16} stroke={1.8} aria-hidden="true" />
        <span>{DRY_RUN_BANNER_TEXT}</span>
      </aside>
    );
  }

  return (
    <aside className="bonds-dryrun-banner bonds-live-banner" role="status" data-testid="live-banner">
      <IconBolt size={16} stroke={1.8} aria-hidden="true" />
      <span>{LIVE_BANNER_TEXT}</span>
      {onStop && (
        <Button
          size="xs"
          color="red"
          ml="auto"
          loading={busy}
          onClick={async () => {
            setBusy(true);
            try {
              await onStop();
            } finally {
              setBusy(false);
            }
          }}
          data-testid="execution-stop"
        >
          Остановить
        </Button>
      )}
    </aside>
  );
}
