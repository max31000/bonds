import type { Plan, PlanLine, PlanSummary, PlansResponse } from '../api/types';

/** Фонд: 26 лотов TMON@ по 166,58 = 4 331,08 ₽ (эталон P1 из бэкенд-тестов). */
export const tmonLine: PlanLine = {
  lineNo: 1,
  broker: 'tinvest',
  accountId: 7,
  instrumentKey: 'RU000TMON001',
  ticker: 'TMON@',
  side: 'Buy',
  quantityLots: 26,
  lotSize: 1,
  quantityPieces: 26,
  limitPrice: 166.58,
  limitPricePoints: null,
  estAmount: 4331.08,
  estFee: 12.99,
  estTax: 0,
  currency: 'RUB',
  reason: 'P1 парковка кэша: доля 50%',
  executionStatus: 'Pending',
  filledLots: 0,
  orders: [],
};

/** Облигация: цена в ПУНКТАХ, рубли — чистая цена за штуку. */
export const bondLine: PlanLine = {
  lineNo: 2,
  broker: 'tinvest',
  accountId: 7,
  instrumentKey: 'RU000A0BOND1',
  ticker: 'RU000A0BOND1',
  side: 'Buy',
  quantityLots: 1,
  lotSize: 1,
  quantityPieces: 1,
  limitPrice: 999.5,
  limitPricePoints: 99.95,
  estAmount: 1009.5,
  estFee: 3.03,
  estTax: 0,
  currency: 'RUB',
  reason: 'P2_INFLOW «Облигации»: кандидат Эмитент, после налога 12,00% годовых',
  executionStatus: 'Pending',
  filledLots: 0,
  orders: [],
};

export const sellLine: PlanLine = {
  lineNo: 1,
  broker: 'tinvest',
  accountId: 7,
  instrumentKey: 'RU000TMON001',
  ticker: 'TMON@',
  side: 'Sell',
  quantityLots: 13,
  lotSize: 1,
  quantityPieces: 13,
  limitPrice: 166.58,
  limitPricePoints: null,
  estAmount: 2165.54,
  estFee: 6.5,
  estTax: 28.02,
  currency: 'RUB',
  reason: 'P5 вывод: тир 0, налог ≈ 1,3% выручки',
  executionStatus: 'Pending',
  filledLots: 0,
  orders: [],
};

const summary: PlanSummary = {
  portfolio: 'rub',
  currency: 'RUB',
  cashBefore: 7340,
  cashAfter: 8.92,
  buyTotal: 7331.08,
  sellTotal: 0,
  feeTotal: 21.99,
  taxTotal: 0,
  requestedNet: null,
  netToWithdraw: null,
  shortfall: null,
  warnings: [],
  notes: [],
  skipped: [],
  pricesPossiblyStale: false,
  executionNote: 'dry-run: заявки брокерам не отправляются',
};

export const sweepPlan: Plan = {
  id: 11,
  rule: 'P1_SWEEP',
  trigger: 'Свободные рубли T-Invest не меньше цены лота корзины «Ликвидность»',
  status: 'PendingApproval',
  dryRun: true,
  createdAt: '2026-10-21T09:00:00Z',
  expiresAt: '2026-10-21T09:30:00Z',
  decidedAt: null,
  decidedVia: null,
  summary,
  lines: [tmonLine],
};

export const inflowPlan: Plan = {
  ...sweepPlan,
  id: 12,
  rule: 'P2_INFLOW',
  trigger: 'Пополнение 2026-10-21 12:00 на 5000 ₽ [dep-1]',
  summary: { ...summary, cashBefore: 5000, cashAfter: 100, buyTotal: 4900, skipped: [{ ticker: 'LQDT', reason: 'нет сделки с известной комиссией в журнале' }] },
  lines: [tmonLine, bondLine],
};

export const withdrawPlan: Plan = {
  ...sweepPlan,
  id: 13,
  rule: 'P5_WITHDRAW',
  trigger: 'Вывод 2000 RUB',
  summary: {
    ...summary,
    cashBefore: 0,
    cashAfter: 2131.02,
    buyTotal: 0,
    sellTotal: 2165.54,
    feeTotal: 6.5,
    taxTotal: 28.02,
    requestedNet: 2000,
    netToWithdraw: 2131.02,
    warnings: ['Налог — оценка по средней цене входа (не FIFO), плоские 13%, без сальдирования и ЛДВ'],
  },
  lines: [sellLine],
};

export const approvedPlan: Plan = {
  ...sweepPlan,
  id: 14,
  status: 'Approved',
  decidedAt: '2026-10-21T09:05:00Z',
  decidedVia: 'web',
  summary: { ...summary, executionNote: 'не исполнен: dry-run' },
};

export const autoPlan: Plan = {
  ...sweepPlan,
  id: 15,
  status: 'AutoApproved',
  decidedAt: '2026-10-21T09:00:00Z',
  decidedVia: 'auto',
};

export const stalePlan: Plan = {
  ...sweepPlan,
  id: 16,
  summary: { ...summary, pricesPossiblyStale: true },
};

export const plansResponse = (items: Plan[]): PlansResponse => ({ items, dryRun: true, disclaimer: 'Режим наблюдения: сделки не отправляются. Оценка, не рекомендация.' });

/** Задача 65: исполняемый план (dryRun = false) — строка частично исполнена второй попыткой. Даты — относительно «сегодня». */
const nowIso = () => new Date().toISOString();
export const executingPlan = (): Plan => ({
  ...sweepPlan,
  id: 21,
  status: 'Submitting',
  dryRun: false,
  createdAt: nowIso(),
  expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
  decidedAt: nowIso(),
  decidedVia: 'web',
  summary: { ...summary, executionNote: 'исполняется' },
  lines: [
    {
      ...tmonLine,
      executionStatus: 'PartiallyFilled',
      filledLots: 10,
      orders: [
        {
          attempt: 1, status: 'Cancelled', limitPrice: 166.58, priceUnit: 'currency', lotsRequested: 26, lotsFilled: 4, avgFillPrice: 166.58, executedCommission: 0,
          currency: 'RUB', errorCode: 'reprice', errorMessage: 'перевыставление', createdAt: nowIso(), updatedAt: nowIso(),
        },
        {
          attempt: 2, status: 'PartiallyFilled', limitPrice: 166.6, priceUnit: 'currency', lotsRequested: 22, lotsFilled: 6, avgFillPrice: 166.6, executedCommission: 0,
          currency: 'RUB', errorCode: null, errorMessage: null, createdAt: nowIso(), updatedAt: nowIso(),
        },
      ],
    },
  ],
});

export const failedBondPlan = (): Plan => ({
  ...executingPlan(),
  id: 22,
  status: 'Failed',
  lines: [
    {
      ...bondLine,
      executionStatus: 'Failed',
      filledLots: 0,
      orders: [
        {
          attempt: 1, status: 'Failed', limitPrice: 99.95, priceUnit: 'points', lotsRequested: 1, lotsFilled: 0, avgFillPrice: null, executedCommission: null,
          currency: 'RUB', errorCode: 'stale_quote', errorMessage: 'Нет свежей котировки (стакан пуст или устарел)', createdAt: nowIso(), updatedAt: nowIso(),
        },
      ],
    },
  ],
});

export const livePlansResponse = (items: Plan[]): PlansResponse => ({
  items,
  dryRun: false,
  disclaimer: 'Исполнение включено: подтверждённые планы отправляются брокеру лимитными заявками. Оценка, не рекомендация.',
});
