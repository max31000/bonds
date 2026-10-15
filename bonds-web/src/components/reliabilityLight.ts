import type { ReliabilityLevel } from '../api/types';

/** Цвет светофора надёжности на статусных токенах кита (`--mvv-success|warning|danger`) — одинаково читается в light/dark. */
export const RELIABILITY_COLOR: Record<ReliabilityLevel, string> = {
  Green: 'var(--mvv-success)',
  Yellow: 'var(--mvv-warning)',
  Red: 'var(--mvv-danger)',
};

export const RELIABILITY_ARIA_LABEL: Record<ReliabilityLevel, string> = {
  Green: 'надёжность: зелёный',
  Yellow: 'надёжность: жёлтый',
  Red: 'надёжность: красный',
};

/** Подпись уровня для шапки карточки скоркарты. */
export const RELIABILITY_TEXT: Record<ReliabilityLevel, string> = {
  Green: 'Зелёный',
  Yellow: 'Жёлтый',
  Red: 'Красный',
};
