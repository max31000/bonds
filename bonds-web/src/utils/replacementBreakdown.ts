import { formatDate, formatHorizon, formatYearsWord } from './format';
import type { ReplacementResponse } from '../api/types';

/**
 * Поля построчной формулы «держать vs переложиться» (математика v2, задача 48) — общие для карточки
 * кандидата (Recommendations) и карточки market-сравнивалки (MarketComparator): подмножество
 * <see>ReplacementResponse</see> плюс единая «представительная» ставка комиссии. Вытащено в один
 * компонент, чтобы обе карточки рендерили ОДИНАКОВУЮ формулу без копипасты.
 * Единицы — как у ответа бэкенда: доходности/ставки — ДОЛИ, деньги — рубли, горизонты — годы.
 */
export interface ReplacementBreakdownData {
  spreadFraction: number | null;
  /** Вложено в цель после всех издержек, рубли. */
  capitalRub: number;
  /** Горизонт, заданный пользователем/настройкой, годы. */
  horizonYears: number;
  horizonSource: 'Request' | 'Settings';
  /** Горизонт, на котором реально сравнены ноги (урезан сроком цели), годы. */
  effectiveHorizonYears: number;
  horizonLimitedByTarget: boolean;
  targetHorizonDate: string | null;
  targetHorizonIsOffer: boolean;
  grossGainRub: number | null;
  sellCommissionRub: number;
  buyCommissionRub: number;
  sellSlippageRub: number;
  buySlippageRub: number;
  slippageUnknown: boolean;
  netBenefitRub: number;
  annualizedBenefitFraction: number | null;
  commissionRateUsed: number;
  commissionRateSource: ReplacementResponse['commissionRateSource'];
  sellTaxEstimateRub: number | null;
  netBenefitAfterTaxRub: number | null;
  couponTaxRate: number;
  holdYieldAfterTaxFraction: number;
  targetYieldAfterTaxFraction: number;
  durationMismatch: boolean;
  durationMismatchWarning: string | null;
}

/**
 * Данные разбивки из ответа POST /analytics/replacement. Бэкенд отдаёт раздельные ставки продажи/покупки
 * (обычно совпадают — единый резолвер применяет одну ставку к обеим сделкам); разбивка показывает одну
 * ставку — ставку продажи как представительную.
 */
export function toBreakdownData(r: ReplacementResponse): ReplacementBreakdownData {
  return {
    spreadFraction: r.spreadFraction,
    capitalRub: r.capitalRub,
    horizonYears: r.horizonYears,
    horizonSource: r.horizonSource,
    effectiveHorizonYears: r.effectiveHorizonYears,
    horizonLimitedByTarget: r.horizonLimitedByTarget,
    targetHorizonDate: r.targetHorizonDate ?? null,
    targetHorizonIsOffer: r.targetHorizonIsOffer,
    grossGainRub: r.grossGainRub,
    sellCommissionRub: r.sellCommissionRub,
    buyCommissionRub: r.buyCommissionRub,
    sellSlippageRub: r.sellSlippageRub,
    buySlippageRub: r.buySlippageRub,
    slippageUnknown: r.slippageUnknown,
    netBenefitRub: r.netBenefitRub,
    annualizedBenefitFraction: r.annualizedBenefitFraction,
    commissionRateUsed: r.sellCommissionRateUsed,
    commissionRateSource: r.commissionRateSource,
    sellTaxEstimateRub: r.sellTaxEstimateRub,
    netBenefitAfterTaxRub: r.netBenefitAfterTaxRub,
    couponTaxRate: r.couponTaxRate,
    holdYieldAfterTaxFraction: r.holdYieldAfterTaxFraction,
    targetYieldAfterTaxFraction: r.targetYieldAfterTaxFraction,
    durationMismatch: r.durationMismatch,
    durationMismatchWarning: r.durationMismatchWarning ?? null,
  };
}

/**
 * Подпись горизонта результата: «на горизонте 2 года (настройка)» и, если срок цели короче, причина обрезки
 * («сокращён до 9 мес: оферта цели 01.05.2027»). Горизонт — не фронтовая константа, а настройка пользователя.
 */
export function horizonCaption(
  r: Pick<
    ReplacementBreakdownData,
    'horizonYears' | 'horizonSource' | 'effectiveHorizonYears' | 'horizonLimitedByTarget' | 'targetHorizonDate' | 'targetHorizonIsOffer'
  >,
): string {
  const source = r.horizonSource === 'Settings' ? 'настройка' : 'запрос';
  const base = `на горизонте ${formatYearsWord(r.horizonYears)} (${source})`;
  if (!r.horizonLimitedByTarget) return base;
  const reason = r.targetHorizonIsOffer ? 'оферта цели' : 'погашение цели';
  const date = r.targetHorizonDate ? ` ${formatDate(r.targetHorizonDate)}` : '';
  return `${base}, сокращён до ${formatHorizon(r.effectiveHorizonYears)}: ${reason}${date}`;
}

/** Сведение разбивки: недостающие строки, чтобы сумма строк давала ровно «чистую выгоду». */
export interface BreakdownReconciliation {
  /**
   * «Эффект роста издержек на горизонте» = netBenefit − (валовая − комиссии − слиппедж), рубли. Валовая выгода считается на
   * полной стоимости hold, а издержки (комиссии, слиппедж) вычитаются из капитала сегодня и тем самым лишают его роста на
   * горизонте (FV-математика v2) — их влияние на чистую выгоду больше номинальной суммы издержек. Null — валовой выгоды нет
   * (yieldDataIncomplete): сводить нечего.
   */
  costGrowthEffectRub: number | null;
  /**
   * Налоговая строка «налог продажи (с учётом роста на горизонте)» = netBenefitRub − netBenefitAfterTaxRub, рубли. Налог
   * вычитается из выручки сегодня, поэтому на выгоду на горизонте влияет больше, чем сам налог. Null — налог не оценён.
   */
  saleTaxRub: number | null;
}

const roundKopecks = (v: number) => Math.round(v * 100) / 100;

/**
 * Считает сводящие строки разбивки (см. <see>BreakdownReconciliation</see>). Единицы — рубли; округление до копеек,
 * чтобы сумма строк (валовая − комиссии − слиппедж + эффект) точно равнялась netBenefitRub.
 */
export function reconcileBreakdown(
  data: Pick<
    ReplacementBreakdownData,
    | 'grossGainRub'
    | 'sellCommissionRub'
    | 'buyCommissionRub'
    | 'sellSlippageRub'
    | 'buySlippageRub'
    | 'netBenefitRub'
    | 'netBenefitAfterTaxRub'
    | 'sellTaxEstimateRub'
  >,
): BreakdownReconciliation {
  const costGrowthEffectRub =
    data.grossGainRub === null
      ? null
      : roundKopecks(
          data.netBenefitRub -
            (data.grossGainRub -
              (data.sellCommissionRub + data.buyCommissionRub) -
              (data.sellSlippageRub + data.buySlippageRub)),
        );
  const saleTaxRub =
    data.sellTaxEstimateRub === null || data.netBenefitAfterTaxRub === null
      ? null
      : roundKopecks(data.netBenefitRub - data.netBenefitAfterTaxRub);
  return { costGrowthEffectRub, saleTaxRub };
}
