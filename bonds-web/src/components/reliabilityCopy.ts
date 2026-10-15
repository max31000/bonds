/**
 * Обязательный дисклеймер скоркарты надёжности (задача 47 закрывает FOLLOWUP задачи 46: прежний текст «по
 * биржевой статистике» устарел — скоркарта строится по MOEX, отчётности ГИР БО и данным брокера). Владелец явно
 * запретил выдавать светофор за кредитный рейтинг — формулировка показывается в тултипе точки, в подвале
 * <see>ReliabilityScoreCard</see> и в <see>RiskSignalsCaption</see>.
 */
export const RELIABILITY_DISCLAIMER =
  'Оценка по открытым данным: MOEX, отчётность ГИР БО, T-Invest; не кредитный рейтинг и не рекомендация.';

/** Словарь критических флагов скоркарты (краснят светофор) → человекочитаемый текст. Ключи зеркалят backend `ReliabilityFlags`. */
export const CRITICAL_FLAG_LABELS: Record<string, string> = {
  IssuerDefaulted: 'Эмитент допускал дефолт',
  InterestUncovered: 'Прибыль не покрывает проценты',
  NegativeEquity: 'Отрицательный капитал',
  SpreadShock: 'Резкое расширение G-спреда',
};

/** Словарь предупреждений (светофор не краснят). */
export const WARNING_FLAG_LABELS: Record<string, string> = {
  IssuerTechDefault: 'Технический дефолт эмитента',
  JunkRating: 'Рейтинг ниже инвестиционного',
  PriceDrawdown: 'Просадка цены',
};

/** Подпись флага; неизвестный ключ (бэкенд добавил флаг раньше фронта) показывается как есть, а не теряется. */
export function flagLabel(key: string): string {
  return CRITICAL_FLAG_LABELS[key] ?? WARNING_FLAG_LABELS[key] ?? key;
}
