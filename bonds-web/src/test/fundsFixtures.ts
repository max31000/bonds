import type { FundDetail, FundRow, FundsResponse, PortfolioSummary } from '../api/types';

/** Дата ISO (yyyy-MM-dd) относительно «сегодня» — в тестах не должно быть абсолютных дат. */
export function isoDaysAgo(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  return d.toISOString().slice(0, 10);
}

export const baseFund: FundRow = {
  fundId: 7,
  ticker: 'TPAY',
  name: 'Пассивный доход',
  managementCompany: 'Т-Капитал',
  quantity: 100,
  currentPriceRub: 101.35,
  priceAsOf: new Date().toISOString(),
  marketValueRub: 10135,
  avgCostRub: 100,
  investedRub: 10000,
  unrealizedPnlRub: 135,
  unrealizedPnlFraction: 0.0135,
  distributionsGrossRub: 360,
  taxesRub: -46.8,
  distributionsNetRub: 313.2,
  totalResultRub: 448.2,
  totalReturnFraction: 0.0448,
  xirrFraction: 0.131,
  totalExpenseFraction: 0.0079,
  performance: {
    m1: { returnFraction: 0.012, annualizedFraction: 0.154, days: 30 },
    m3: { returnFraction: 0.035, annualizedFraction: 0.15, days: 92 },
    m6: null,
    m12: { returnFraction: 0.1425, annualizedFraction: 0.1425, days: 365 },
    sinceFirstBuy: { returnFraction: 0.05, annualizedFraction: 0.12, days: 150 },
  },
  outlook: {
    requestedMethod: 'auto',
    method: 'distributions',
    expectedAnnualYieldFraction: 0.1488,
    afterTaxYieldFraction: 0.1295,
    basisLabel: 'по последним выплатам за 3 мес.',
    expectedIncome1yRub: 1508,
    expectedMonthlyIncomeRub: 120,
    confidence: 'high',
    asOf: isoDaysAgo(0),
    warnings: [],
    unavailableReason: null,
  },
  shareOfPortfolioFraction: 0.4,
  hasUnknownLots: false,
  dataIncomplete: false,
};

export const baseFunds: FundsResponse = {
  asOf: isoDaysAgo(0),
  funds: [baseFund],
  totals: { marketValueRub: 10135, investedRub: 10000, unrealizedPnlRub: 135, incomeNetRub: 313.2, totalResultRub: 448.2, dataIncomplete: false },
  disclaimer: 'Оценка, не рекомендация.',
};

export const baseFundDetail: FundDetail = {
  asOf: isoDaysAgo(0),
  fund: baseFund,
  priceHistory: [
    { date: isoDaysAgo(60), closeRub: 100 },
    { date: isoDaysAgo(30), closeRub: 100.8 },
    { date: isoDaysAgo(0), closeRub: 101.35 },
  ],
  distributions: [
    { recordDate: isoDaysAgo(50), paymentDate: isoDaysAgo(45), amountPerShareRub: 1.2, yieldOnPriceFraction: 0.012 },
    { recordDate: isoDaysAgo(20), paymentDate: isoDaysAgo(15), amountPerShareRub: 1.2, yieldOnPriceFraction: 0.0119 },
  ],
  operations: [
    { date: `${isoDaysAgo(150)}T10:00:00`, type: 'buy', amountRub: -10000, quantity: 100 },
    { date: `${isoDaysAgo(50)}T10:00:00`, type: 'distribution', amountRub: 120, quantity: null },
  ],
  settings: { forecastMethod: 'auto', benchmarkIndex: null },
  disclaimer: 'Оценка, не рекомендация.',
};

export const baseSummary: PortfolioSummary = {
  bondsValueRub: 105000,
  fundsValueRub: 10135,
  cashRub: 8000,
  totalRub: 123135,
  brokerTotalRub: 123135,
  brokerDiffRub: 0,
  balanceSyncedAt: new Date().toISOString(),
  portfolioXirrFraction: 0.125,
  fundsDataIncomplete: false,
  disclaimer: '',
};
