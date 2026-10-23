import type { BrokerHoldingsResponse, BrokerPositionRow, Capabilities } from '../api/types';

export const capsFreedomOn: Capabilities = { tinvestRead: true, tinvestTrade: false, freedom: true, aiAgents: false };
export const capsFreedomOff: Capabilities = { tinvestRead: true, tinvestTrade: false, freedom: false, aiAgents: false };

export const luvUsd: BrokerPositionRow = {
  ticker: 'LUV.US',
  name: 'Southwest Airlines',
  assetClass: 'stock',
  currency: 'USD',
  quantity: 13,
  avgPrice: 40.3,
  lastPrice: 41.56,
  marketValue: 540.28,
  unrealizedPnl: 16.38,
  unrealizedPnlFraction: 0.0313,
  marketValueRub: 43222.4,
};

export const roboEur: BrokerPositionRow = {
  ticker: 'ROBO.EU',
  name: 'L&G ROBO Global Robotics and Automation UCITS ETF',
  assetClass: 'etf',
  currency: 'EUR',
  quantity: 3,
  avgPrice: 24.4075,
  lastPrice: 27.07,
  marketValue: 81.21,
  unrealizedPnl: 7.99,
  unrealizedPnlFraction: 0.1091,
  marketValueRub: 7308.9,
};

export const vukeGbp: BrokerPositionRow = {
  ticker: 'VUKE.UK',
  name: 'Vanguard FTSE 100 UCITS ETF',
  assetClass: 'etf',
  currency: 'GBP',
  quantity: 2,
  avgPrice: 30,
  lastPrice: 36,
  marketValue: 72,
  unrealizedPnl: 12,
  unrealizedPnlFraction: 0.2,
  marketValueRub: 7920,
};

/** Счёт Freedom: позиции в USD, EUR и GBP + деньги USD/EUR; итог ≈ 2 800 $ (курс USD 80). */
export const baseFreedom: BrokerHoldingsResponse = {
  positions: [luvUsd, roboEur, vukeGbp],
  cash: [
    { currency: 'USD', amount: 4.77, amountRub: 381.6 },
    { currency: 'EUR', amount: 88.57, amountRub: 7971.3 },
  ],
  totalValueRub: 66804.2,
  totalValueUsd: 835.05,
  syncedAt: new Date().toISOString(),
  dataIncomplete: false,
  fees: { feesPaidUsd: 3.5, dataIncomplete: false },
  disclaimer: 'Оценка, не рекомендация.',
};
