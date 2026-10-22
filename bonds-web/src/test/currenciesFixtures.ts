import type { CurrenciesResponse, CurrencyRow } from '../api/types';
import { isoDaysAgo } from './fundsFixtures';

/** Юани: 1000 CNY по 11,50 ₽ (средний курс 11,00 ₽), история курса — относительно «сегодня». */
export const baseCurrency: CurrencyRow = {
  currencyId: 3,
  isoCode: 'CNY',
  ticker: 'CNYRUB_TOM',
  name: 'Китайский юань',
  quantity: 1000,
  currentPriceRub: 11.5,
  priceAsOf: isoDaysAgo(0),
  marketValueRub: 11500,
  avgCostRub: 11,
  investedRub: 11000,
  unrealizedPnlRub: 500,
  unrealizedPnlFraction: 0.04545,
  realizedPnlRub: 120,
  totalResultRub: 620,
  totalReturnFraction: 0.0564,
  xirrFraction: 0.09,
  rateChange: { m1: 0.02, m3: -0.015, m12: null },
  rateHistory: [
    { date: isoDaysAgo(90), closeRub: 11.7 },
    { date: isoDaysAgo(30), closeRub: 11.27 },
    { date: isoDaysAgo(0), closeRub: 11.5 },
  ],
  hasUnknownLots: false,
  dataIncomplete: false,
};

export const soldCurrency: CurrencyRow = {
  ...baseCurrency,
  currencyId: 4,
  isoCode: 'HKD',
  ticker: null,
  name: 'Гонконгский доллар',
  quantity: 0,
  currentPriceRub: 1.4,
  marketValueRub: 0,
  avgCostRub: 0,
  investedRub: 0,
  unrealizedPnlRub: 0,
  unrealizedPnlFraction: null,
  realizedPnlRub: -35,
  totalResultRub: -35,
  rateChange: { m1: null, m3: null, m12: null },
  rateHistory: [],
};

export const baseCurrencies: CurrenciesResponse = {
  asOf: isoDaysAgo(0),
  currencies: [baseCurrency],
  totals: { marketValueRub: 11500, investedRub: 11000, unrealizedPnlRub: 500, realizedPnlRub: 120, totalResultRub: 620, dataIncomplete: false },
  disclaimer: 'Оценка, не рекомендация.',
};
