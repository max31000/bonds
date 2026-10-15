import type { GspreadSource, UniverseHiddenReason, UniverseRow } from '../api/types';

/** Русская подпись бейджа ликвидности (LiquidityScore) — общая для MarketComparator (27) и Screener (28). */
export function liquidityLabel(score: UniverseRow['liquidityScore']): string {
  switch (score) {
    case 'High':
      return 'высокая ликвидность';
    case 'Medium':
      return 'средняя ликвидность';
    case 'Low':
      return 'низкая ликвидность';
    default:
      return 'ликвидность неизвестна';
  }
}

export function liquidityColor(score: UniverseRow['liquidityScore']): string {
  switch (score) {
    case 'High':
      return 'teal';
    case 'Medium':
      return 'yellow';
    case 'Low':
      return 'red';
    default:
      return 'gray';
  }
}

/** Подпись источника G-спреда банка (задача 42) для тултипа; null — спреда нет, подписывать нечего. */
export function gspreadSourceLabel(source: GspreadSource | null | undefined): string | null {
  switch (source) {
    case 'moex':
      return 'биржевой';
    case 'approx':
      return 'расчётный (по кривой)';
    default:
      return null;
  }
}

/** Русская подпись причины скрытия бумаги гигиеническим фильтром (статусная строка/бейдж скринера). */
export function hiddenReasonLabel(reason: UniverseHiddenReason | null): string {
  switch (reason) {
    case 'LowTurnover':
      return 'низкий оборот';
    case 'ListLevelThree':
      return 'некотировальный список (3-й уровень листинга)';
    case 'ImplausibleYield':
      return 'аномальная доходность';
    case 'MissingDurationOrPrice':
      return 'нет данных дюрации/цены';
    case 'NearMaturity':
      return 'близко к погашению/оферте';
    default:
      return '—';
  }
}
