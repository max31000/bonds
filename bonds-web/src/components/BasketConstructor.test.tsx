import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { BasketConstructor } from './BasketConstructor';
import type { BasketResponse, UniverseRow } from '../api/types';

function renderBasketConstructor() {
  return render(
    <MantineProvider>
      <BasketConstructor />
    </MantineProvider>,
  );
}

const strongBondRow: UniverseRow = {
  secid: 'SU26238',
  isin: 'RU000A1038V6',
  name: 'Сильная бумага',
  sector: 'Корпоративные',
  yieldFraction: 0.16,
  durationYears: 2.2,
  pricePercent: 95,
  turnoverRub: 1_000_000,
  listLevel: 1,
  liquidityScore: 'High',
  slippageEstimateFraction: 0.001,
  gspreadApproxFraction: 0.02,
  maturityDate: '2028-12-01',
  offerDate: null,
  isHidden: false,
  hiddenReason: null,
  inPortfolio: false,
  inWatchlist: false,
};

const basketResult: BasketResponse = {
  basket: {
    amountRub: 15000,
    lines: [
      {
        instrumentId: 11,
        name: 'Сильная бумага',
        issuer: 'Хороший Эмитент',
        targetWeightFraction: 1.0,
        actualWeightFraction: 0.9,
        quantity: 10,
        actualCostRub: 13500,
        effectiveYield: 0.16,
        modifiedDuration: 2.2,
        isFloater: false,
        lotSizeAssumed: true,
        cleanCostRub: 12800,
        accruedCostRub: 500,
        commissionCostRub: 200,
      },
    ],
    leftoverRub: 1500,
    metrics: { totalCostRub: 13500, weightedYield: 0.16, weightedDuration: 2.2, hasExcludedFloaters: false },
  },
  whatIf: {
    before: { totalValueRub: 100000, weightedYield: 0.144, weightedDuration: 1.2, hasExcludedFloaters: false },
    after: { totalValueRub: 113500, weightedYield: 0.151, weightedDuration: 1.6, hasExcludedFloaters: false },
    concentrations: [{ issuer: 'Хороший Эмитент', sharePercentBefore: 10, sharePercentAfter: 22 }],
    warnings: [],
  },
  disclaimer: 'Расчёт корзины — не инвестиционная рекомендация.',
};

describe('BasketConstructor', () => {
  beforeEach(() => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [strongBondRow], total: 1, hiddenCount: 0 })),
      http.post('*/api/universe/:secid/materialize', () =>
        HttpResponse.json({
          instrumentId: 11,
          secid: 'SU26238',
          isin: 'RU000A1038V6',
          metrics: { name: 'Сильная бумага', issuer: 'Хороший Эмитент' },
          disclaimer: '',
        }),
      ),
    );
  });

  it('adds a line to the basket from the server-backed search and materializes it', async () => {
    renderBasketConstructor();

    await waitFor(() => expect(screen.getByTestId('basket-search-select')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('basket-search-select'), { target: { value: 'Сильная' } });
    fireEvent.click(await screen.findByText('Сильная бумага'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-11')).toBeInTheDocument());
    expect(screen.getByTestId('basket-draft-line-11').textContent).toMatch(/Сильная бумага/);
  });

  it('highlights the total when weights do not sum to 100%', async () => {
    renderBasketConstructor();

    fireEvent.change(screen.getByTestId('basket-search-select'), { target: { value: 'Сильная' } });
    fireEvent.click(await screen.findByText('Сильная бумага'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-weight-11')).toBeInTheDocument());
    fireEvent.change(screen.getByTestId('basket-draft-weight-11'), { target: { value: '40' } });

    await waitFor(() => expect(screen.getByTestId('basket-total-percent').textContent).toMatch(/40\.0%/));
    expect(screen.getByTestId('basket-total-percent').textContent).toMatch(/должно быть 100%/);
  });

  it('normalizes weights to sum to 100%', async () => {
    renderBasketConstructor();

    fireEvent.change(screen.getByTestId('basket-search-select'), { target: { value: 'Сильная' } });
    fireEvent.click(await screen.findByText('Сильная бумага'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-weight-11')).toBeInTheDocument());
    fireEvent.change(screen.getByTestId('basket-draft-weight-11'), { target: { value: '40' } });

    fireEvent.click(screen.getByTestId('basket-normalize-button'));

    await waitFor(() => expect(screen.getByTestId('basket-total-percent').textContent).toMatch(/100\.0%/));
  });

  it('fills the basket from the greedy allocation preset', async () => {
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({
          amountRub: 15000,
          allocations: [
            {
              instrumentId: 11,
              name: 'Сильная бумага',
              issuer: 'Хороший Эмитент',
              quantity: 10,
              estimatedCostRub: 10000,
              effectiveYield: 0.16,
              lotSizeAssumed: true,
              cleanCostRub: 9500,
              accruedCostRub: 400,
              commissionCostRub: 100,
            },
          ],
          skipped: [],
          leftoverRub: 5000,
          disclaimer: '',
        }),
      ),
    );

    renderBasketConstructor();

    fireEvent.click(screen.getByTestId('basket-preset-button'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-11')).toBeInTheDocument());
    // estimatedCostRub 10000 / amountRub 15000 * 100 ≈ 66.7%
    const weightInput = screen.getByTestId('basket-draft-weight-11') as HTMLInputElement;
    expect(Number(weightInput.value)).toBeCloseTo(66.67, 1);
  });

  it('calculates the basket and renders quantities, metrics, and the what-if block', async () => {
    server.use(http.post('*/api/analytics/basket', () => HttpResponse.json(basketResult)));

    renderBasketConstructor();

    fireEvent.change(screen.getByTestId('basket-search-select'), { target: { value: 'Сильная' } });
    fireEvent.click(await screen.findByText('Сильная бумага'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-weight-11')).toBeInTheDocument());
    fireEvent.change(screen.getByTestId('basket-draft-weight-11'), { target: { value: '100' } });

    fireEvent.click(screen.getByTestId('basket-calculate-button'));

    await waitFor(() => expect(screen.getByTestId('basket-result')).toBeInTheDocument());
    expect(screen.getByTestId('basket-result-line-11').textContent).toMatch(/10/);
    expect(screen.getByTestId('basket-metrics').textContent).toMatch(/16\.00%/);
    expect(screen.getByTestId('basket-whatif').textContent).toMatch(/14\.40%/);
    expect(screen.getByTestId('basket-whatif').textContent).toMatch(/15\.10%/);
  });

  it('shows a warning badge when the what-if response includes a concentration warning', async () => {
    server.use(
      http.post('*/api/analytics/basket', () =>
        HttpResponse.json({
          ...basketResult,
          whatIf: {
            ...basketResult.whatIf,
            warnings: [{ kind: 'ConcentrationLimitBreached', issuer: 'Хороший Эмитент', sharePercentAfter: 40 }],
          },
        }),
      ),
    );

    renderBasketConstructor();

    fireEvent.change(screen.getByTestId('basket-search-select'), { target: { value: 'Сильная' } });
    fireEvent.click(await screen.findByText('Сильная бумага'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-weight-11')).toBeInTheDocument());
    fireEvent.change(screen.getByTestId('basket-draft-weight-11'), { target: { value: '100' } });
    fireEvent.click(screen.getByTestId('basket-calculate-button'));

    await waitFor(() => expect(screen.getByTestId('basket-whatif-warnings')).toBeInTheDocument());
    expect(screen.getByTestId('basket-whatif-warning-0').textContent).toMatch(/⚠️/);
    expect(screen.getByTestId('basket-whatif-warning-0').textContent).toMatch(/Хороший Эмитент/);
  });

  it('shows a validation error when POST /api/analytics/basket returns 422', async () => {
    server.use(
      http.post('*/api/analytics/basket', () =>
        HttpResponse.json({ error: 'Сумма весов строк не может превышать 1', type: 'ValidationException' }, { status: 422 }),
      ),
    );

    renderBasketConstructor();

    fireEvent.change(screen.getByTestId('basket-search-select'), { target: { value: 'Сильная' } });
    fireEvent.click(await screen.findByText('Сильная бумага'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-weight-11')).toBeInTheDocument());
    fireEvent.change(screen.getByTestId('basket-draft-weight-11'), { target: { value: '100' } });
    fireEvent.click(screen.getByTestId('basket-calculate-button'));

    await waitFor(() => expect(screen.getByTestId('basket-calculate-error')).toBeInTheDocument());
    expect(screen.getByTestId('basket-calculate-error').textContent).toMatch(/Сумма весов/);
  });
});
