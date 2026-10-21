import type { FundForecastMethod, FundOperationType, FundOutlook, FundOutlookConfidence, FundOutlookWarning } from '../api/types';

/** Подписи способов прогноза (в селекте и бейдже). */
export const FUND_METHOD_LABEL: Record<FundForecastMethod, string> = {
  auto: 'Авто',
  distributions: 'По выплатам',
  indexYield: 'По индексу',
  trailingReturn: 'По факту',
};

export const FUND_METHOD_OPTIONS: { value: FundForecastMethod; label: string }[] = [
  { value: 'auto', label: FUND_METHOD_LABEL.auto },
  { value: 'distributions', label: FUND_METHOD_LABEL.distributions },
  { value: 'indexYield', label: FUND_METHOD_LABEL.indexYield },
  { value: 'trailingReturn', label: FUND_METHOD_LABEL.trailingReturn },
];

export const FUND_CONFIDENCE_LABEL: Record<FundOutlookConfidence, string> = {
  low: 'низкая',
  medium: 'средняя',
  high: 'высокая',
};

export const FUND_WARNING_LABEL: Record<FundOutlookWarning, string> = {
  terUnknown: 'Комиссия фонда (TER) неизвестна — принята равной нулю, оценка может быть завышена.',
  indexStale: 'Данные по индексу устарели (более 10 дней).',
  fewDistributions: 'Мало выплат для надёжной оценки по выплатам.',
  shortHistory: 'История цен короче года — доходность пересчитана в годовую по короткому окну.',
};

export const FUND_OPERATION_LABEL: Record<FundOperationType, string> = {
  buy: 'Покупка',
  sell: 'Продажа',
  distribution: 'Выплата',
  tax: 'Налог',
  fee: 'Комиссия',
  securityIn: 'Ввод паёв',
  securityOut: 'Вывод паёв',
};

/** Текст про зависимость выплат от ключевой ставки (фонды с переменным купоном, напр. «Пассивный доход»). */
export const FUND_KEY_RATE_NOTE =
  'Для фондов с переменным купоном выплаты следуют за ключевой ставкой: при её снижении они будут ниже, чем в недавнем прошлом. ' +
  'Оценка — не гарантия и не инвестиционная рекомендация.';

export const FUND_AFTER_TAX_NOTE = 'После налога — упрощённо: НДФЛ 13% применён ко всей доходности (без льгот и прогрессии).';

/** Подпись для тултипа оценки: основа, после налога, уверенность, предупреждения. */
export function outlookTooltip(o: FundOutlook, afterTaxFormatted: string): string {
  if (o.expectedAnnualYieldFraction === null) return o.unavailableReason ?? 'Оценка недоступна';
  const parts = [`Основа: ${o.basisLabel}.`, `После НДФЛ ≈ ${afterTaxFormatted}.`, `Уверенность: ${FUND_CONFIDENCE_LABEL[o.confidence]}.`];
  for (const w of o.warnings) parts.push(FUND_WARNING_LABEL[w]);
  return parts.join(' ');
}

/** Предупреждение, когда у фонда нет цены пая (ни в позиции, ни в истории): стоимость учтена как 0, итоги занижены. */
export const FUND_DATA_INCOMPLETE =
  'Нет цены пая (ни от брокера, ни в истории торгов) — стоимость учтена как 0, P&L и итоги приблизительные.';
