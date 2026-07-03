import type { PositionRow } from '../api/types';

/**
 * Хелперы/константы отображения таблицы позиций и мобильных карточек, общие для Positions.tsx и
 * PositionCard.tsx. Вынесено в utils/ (не components/positionsShared.tsx, где живёт
 * LiveMarketValueCell) из-за react-refresh/only-export-components — файл компонента может
 * экспортировать только React-компоненты, всё остальное сюда.
 */

export type SortKey = 'yield' | 'pnl';
export type SortState = { key: SortKey; direction: 'asc' | 'desc' };

/** Эффективная доходность для отображения/сортировки: currentYield для floater/indexed, иначе ytmEffective. */
export function effectiveYield(row: PositionRow): number | null {
  if (row.isFloater || row.isIndexed) return row.currentYield;
  return row.ytmEffective;
}

export const COUPON_TYPE_LABEL: Record<PositionRow['couponType'], string> = {
  Fixed: 'Фиксированный',
  Floating: 'Плавающий',
  Indexed: 'Индексируемый',
};

export const YIELD_TOOLTIP_TEXT =
  'YTM — эффективная доходность к погашению/оферте от текущей рыночной цены. ' +
  'Не зависит от вашей цены покупки. Доход от цены входа — колонка P&L.';
