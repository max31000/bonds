import type { BasketsResponse, CapitalBasket, CapitalPortfolio } from '../api/types';

const base = {
  isSystem: false,
  liquidityTier: 0,
  fillRule: 'fixed' as const,
  autoBuy: false,
  autoSell: false,
  minTrade: 0,
  members: [],
  positions: [],
};

/** Эталон из плана: портфель 1 000 000 ₽, ликвидность 120 000 при цели 0.15 → drift −0.03, Below, дефицит 30 000 (полоса 0.02). */
export const liquidityBasket: CapitalBasket = {
  ...base,
  id: 1,
  code: 'liquidity',
  title: 'Ликвидность',
  targetWeight: 0.15,
  band: 0.02,
  hardBand: 0.05,
  value: 120_000,
  weight: 0.12,
  shareOfTotal: 0.12,
  target: 0.15,
  drift: -0.03,
  state: 'below',
  deficitToTarget: 30_000,
  members: [
    { id: 11, broker: 'tinvest', instrumentKey: 'TMON@', subWeight: 0.5, matchRule: 'instrument', value: 120_000, actualSubShare: 1 },
    { id: 12, broker: 'tinvest', instrumentKey: 'TSAV', subWeight: 0.5, matchRule: 'instrument', value: null, actualSubShare: null },
  ],
};

export const bondsBasket: CapitalBasket = {
  ...base,
  id: 2,
  code: 'bonds',
  title: 'Облигации',
  liquidityTier: 2,
  fillRule: 'engine',
  targetWeight: 0.85,
  band: 0.02,
  hardBand: 0.05,
  value: 880_000,
  weight: 0.88,
  shareOfTotal: 0.88,
  target: 0.85,
  drift: 0.03,
  state: 'above',
  deficitToTarget: -30_000,
  members: [{ id: 21, broker: 'tinvest', instrumentKey: 'bond', subWeight: null, matchRule: 'asset_class' }],
};

export const temporaryBasket: CapitalBasket = {
  ...base,
  id: 4,
  code: 'temporary',
  title: 'Временные',
  fillRule: 'hold',
  targetWeight: null,
  band: 0.05,
  hardBand: 0.1,
  value: 0,
  weight: 0,
  shareOfTotal: 0,
  target: null,
  drift: null,
  state: 'noTarget',
  deficitToTarget: null,
};

export const cashRow: CapitalBasket = {
  id: null,
  code: 'cash',
  title: 'Свободные деньги',
  isSystem: true,
  targetWeight: null,
  band: null,
  hardBand: null,
  liquidityTier: null,
  fillRule: null,
  autoBuy: null,
  autoSell: null,
  minTrade: null,
  value: 0,
  weight: 0,
  shareOfTotal: 0,
  target: null,
  drift: null,
  state: 'noTarget',
  deficitToTarget: null,
  members: [],
  positions: [],
};

export const rubPortfolio: CapitalPortfolio = {
  portfolio: 'rub',
  currency: 'RUB',
  hasAccount: true,
  totalValue: 1_000_000,
  baseValue: 1_000_000,
  freeCash: 0,
  targetsSum: 1,
  dataIncomplete: false,
  syncedAt: null,
  baskets: [liquidityBasket, bondsBasket, temporaryBasket, cashRow],
};

export const usdPortfolio: CapitalPortfolio = {
  portfolio: 'usd',
  currency: 'USD',
  hasAccount: true,
  totalValue: 1673.75,
  baseValue: 1061.25,
  freeCash: 61.25,
  targetsSum: 1,
  dataIncomplete: false,
  syncedAt: null,
  baskets: [
    {
      ...base,
      id: 101,
      code: 'global_core',
      title: 'Глобальное ядро',
      liquidityTier: 1,
      targetWeight: 1,
      band: 0.05,
      hardBand: 0.1,
      value: 1000,
      weight: 0.94,
      shareOfTotal: 0.6,
      target: 1,
      drift: -0.06,
      state: 'below',
      deficitToTarget: 61.25,
    },
  ],
};

export const baseBaskets: BasketsResponse = {
  portfolios: [rubPortfolio, usdPortfolio],
  disclaimer: 'Оценка, не рекомендация: тестовый дисклеймер сервера.',
};
