import { useState } from 'react';
import { Button } from '@mantine/core';
import { IconAlertTriangle, IconBolt, IconEye } from '@tabler/icons-react';

/** Текст баннера режима наблюдения: заявки брокерам не отправляются. */
export const DRY_RUN_BANNER_TEXT = 'Режим наблюдения: сделки не отправляются';

/** Текст баннера живого режима (задача 65): все ворота открыты, подтверждённые планы уходят брокеру T-Invest. */
export const LIVE_BANNER_TEXT = 'Исполнение включено: подтверждённые планы отправляются брокеру T-Invest';

/** Ворота по брокерам из ответа `/api/plans` (задача 66). */
export interface BrokerLive {
  tinvest: boolean;
  freedom: boolean;
}

/**
 * Текст живого баннера по воротам брокеров: каким брокерам заявки уходят и каким — нет. Нет данных по брокерам (старый ответ) — прежний текст T-Invest.
 */
export function liveBannerText(brokers?: BrokerLive): string {
  if (!brokers) return LIVE_BANNER_TEXT;
  if (brokers.tinvest && brokers.freedom) return 'Исполнение включено: подтверждённые планы отправляются брокерам T-Invest и Freedom';
  if (brokers.freedom) return 'Исполнение включено: подтверждённые планы отправляются брокеру Freedom; T-Invest — заявки не отправляются';
  return `${LIVE_BANNER_TEXT}; Freedom — заявки не отправляются`;
}

/** Баннер активных заявок: показывается при любой заявке, висящей у брокера, — независимо от режима и ворот исполнения. */
export const ACTIVE_ORDERS_BANNER_TEXT = 'Есть активные заявки у брокера';

/** «Есть активные заявки у брокера» — заявки могут исполниться даже в режиме наблюдения/на паузе, пока их не снимут. */
export function ActiveOrdersBanner({ count }: { count: number }) {
  if (count <= 0) return null;
  return (
    <aside className="bonds-dryrun-banner bonds-live-banner" role="alert" data-testid="active-orders-banner">
      <IconAlertTriangle size={16} stroke={1.8} aria-hidden="true" />
      <span>
        {ACTIVE_ORDERS_BANNER_TEXT}: {count}. Пока их не снимут, они могут исполниться — исполнитель снимает их сам при остановке; если не может, придёт
        уведомление снять их в терминале брокера.
      </span>
    </aside>
  );
}

/**
 * Баннер страницы «Планы». `live` берётся из ответа сервера (`dryRun === false` — все ворота исполнения открыты); пока данных нет — режим наблюдения
 * (безопасный дефолт). В живом режиме — кнопка «Остановить» (= `/stop` в боте: пауза, висящие заявки снимаются).
 */
export function DryRunBanner({ live = false, brokers, onStop }: { live?: boolean; brokers?: BrokerLive; onStop?: () => Promise<void> }) {
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
      <span>{liveBannerText(brokers)}</span>
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
