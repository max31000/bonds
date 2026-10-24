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
