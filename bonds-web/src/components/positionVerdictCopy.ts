import type { PositionVerdictKind, VerdictConfidence } from '../api/types';

/**
 * Подписи и цвета вердикта (задача 49). Цвета нейтральные: Hold — серый, BuyMoreCandidate — синий,
 * ConsiderReplace — жёлтый, ConsiderExit — красный. Формулировки — «кандидат/рассмотреть», не «купите/продайте».
 */
export const VERDICT_META: Record<PositionVerdictKind, { label: string; color: string }> = {
  Hold: { label: 'Держать', color: 'gray' },
  BuyMoreCandidate: { label: 'Кандидат на докупку', color: 'blue' },
  ConsiderReplace: { label: 'Рассмотреть замену', color: 'yellow' },
  ConsiderExit: { label: 'Рассмотреть выход', color: 'red' },
};

export const CONFIDENCE_LABEL: Record<VerdictConfidence, string> = {
  Low: 'низкая — данных мало',
  Medium: 'средняя',
  High: 'высокая',
};
