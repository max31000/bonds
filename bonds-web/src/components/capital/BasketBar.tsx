import type { CapitalBasket } from '../../api/types';
import { barGeometry, formatWeight } from '../../utils/baskets';

const pct = (v: number) => `${(v * 100).toFixed(2)}%`;

/**
 * Полоса отклонения корзины: цель (риска), допуск `band` (светлая зона вокруг цели), жёсткий допуск `hardBand` (ещё шире, бледнее) и факт
 * (заливка до текущей доли). Все входы — ДОЛИ (`utils/baskets.barGeometry`), проценты рисуем только в подписи. У корзины без цели — просто
 * заливка доли портфеля.
 */
export function BasketBar({ basket }: { basket: CapitalBasket }) {
  const noTarget = basket.target === null;
  const g = barGeometry({ weight: noTarget ? basket.shareOfTotal : basket.weight, target: basket.target, band: basket.band, hardBand: basket.hardBand });
  const label = noTarget
    ? `Доля портфеля ${formatWeight(basket.shareOfTotal)}, цели нет`
    : `Факт ${formatWeight(basket.weight)}, цель ${formatWeight(basket.target)}, допуск ±${formatWeight(basket.band)}`;

  return (
    <div className="bonds-basket-bar" role="img" aria-label={label} data-testid={`basket-bar-${basket.code}`}>
      {!noTarget && (
        <>
          <div className="bonds-basket-bar__hard" style={{ left: pct(g.hardFrom), width: pct(g.hardTo - g.hardFrom) }} />
          <div className="bonds-basket-bar__band" style={{ left: pct(g.bandFrom), width: pct(g.bandTo - g.bandFrom) }} />
        </>
      )}
      <div className={`bonds-basket-bar__fill bonds-basket-bar__fill--${basket.state}`} style={{ width: pct(g.actual) }} />
      {g.target !== null && <div className="bonds-basket-bar__target" style={{ left: pct(g.target) }} />}
    </div>
  );
}
