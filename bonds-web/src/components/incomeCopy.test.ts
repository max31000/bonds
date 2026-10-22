import { describe, expect, it } from 'vitest';
import { incomeUnknownCostDetail } from './incomeCopy';

const rub = (v: number) => `${v} ₽`;

describe('incomeUnknownCostDetail', () => {
  it('называет исключённую выручку и остаток', () => {
    expect(incomeUnknownCostDetail(137296, 4.5, rub)).toBe(
      'Не входит в результат: выручка 137296 ₽ от продажи без известной покупки; остаток без известной покупки на 4.5 ₽.',
    );
  });

  it('молчит, когда исключать нечего', () => {
    expect(incomeUnknownCostDetail(0, 0, rub)).toBeNull();
  });
});
