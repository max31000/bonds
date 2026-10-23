import { describe, expect, it } from 'vitest';
import { formatLimitPrice, formatPlanMoney, formatQuantity, isDecidable, PLAN_RULE_LABEL, PLAN_STATUS_LABEL } from './plans';

describe('utils/plans (задача 63)', () => {
  it('формат лимитной цены: облигации — пункты и рубли, остальные — только валюта', () => {
    expect(formatLimitPrice({ limitPrice: 999.5, limitPricePoints: 99.95, currency: 'RUB' })).toMatch(/99,95\sп\.\s\(999,50\sRUB\)/);
    expect(formatLimitPrice({ limitPrice: 166.58, limitPricePoints: null, currency: 'RUB' })).toMatch(/^166,58\sRUB$/);
    expect(formatLimitPrice({ limitPrice: 110.02, limitPricePoints: null, currency: 'USD' })).toMatch(/110,02\sUSD/);
  });

  it('количество: лоты, штуки при лоте больше 1', () => {
    expect(formatQuantity({ quantityLots: 26, lotSize: 1, quantityPieces: 26 })).toBe('26 лот.');
    expect(formatQuantity({ quantityLots: 3, lotSize: 10, quantityPieces: 30 })).toBe('3 лот. (30 шт.)');
  });

  it('решить можно только Draft и PendingApproval', () => {
    expect(isDecidable('PendingApproval')).toBe(true);
    expect(isDecidable('Draft')).toBe(true);
    for (const s of ['AutoApproved', 'Approved', 'Rejected', 'Expired', 'Submitting', 'Done', 'Failed'] as const) expect(isDecidable(s)).toBe(false);
  });

  it('подписи покрывают все правила и статусы; деньги — с кодом валюты', () => {
    expect(Object.keys(PLAN_RULE_LABEL)).toHaveLength(7);
    expect(Object.keys(PLAN_STATUS_LABEL)).toHaveLength(9);
    expect(formatPlanMoney(1234.5, 'USD')).toMatch(/1\s234,50\sUSD/);
    expect(formatPlanMoney(null, 'RUB')).toBe('—');
  });
});
