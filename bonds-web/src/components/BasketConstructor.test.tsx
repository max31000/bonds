import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { BasketConstructor } from './BasketConstructor';
import type { BasketResponse, RiskSignals, UniverseRow } from '../api/types';

/** Селектор источника пресета — SegmentedControl рендерится как набор радио-лейблов (Mantine). */
function selectSource(label: string) {
  fireEvent.click(within(screen.getByTestId('basket-source-control')).getByText(label));
}

const goodSignals: RiskSignals = {
  liquidity: 'Good',
  liquidityLabel: 'Высокая ликвидность, листинг 1',
  spread: 'Neutral',
  gSpreadFraction: 0.03,
  spreadVsBasketMedianFraction: 0.001,
  reliability: 'Green',
  reliabilityReason: 'Зелёный: оба риск-сигнала в норме, листинг 1-2, ликвидность с данными.',
};

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
  reliability: 'Green',
  reliabilityReason: 'Зелёный: оба риск-сигнала в норме, листинг 1-2, ликвидность с данными.',
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

  // Задача 35 §C.1: дефолт источника — "market", этот тест покрывает прежнее поведение source=portfolio.
  it('fills the basket from the greedy allocation preset (source=portfolio)', async () => {
    let requestedSource: string | null = null;
    server.use(
      http.get('*/api/analytics/allocation', ({ request }) => {
        requestedSource = new URL(request.url).searchParams.get('source');
        return HttpResponse.json({
          amountRub: 15000,
          source: 'portfolio',
          allocations: [
            {
              instrumentId: 11,
              secid: null,
              name: 'Сильная бумага',
              issuer: 'Хороший Эмитент',
              sector: null,
              quantity: 10,
              estimatedCostRub: 10000,
              effectiveYield: 0.16,
              lotSizeAssumed: true,
              cleanCostRub: 9500,
              accruedCostRub: 400,
              commissionCostRub: 100,
              riskSignals: null,
            },
          ],
          skipped: [],
          leftoverRub: 5000,
          disclaimer: '',
          commissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          candidatePoolAvailable: null,
          candidatePoolLimit: null,
          candidatePoolTruncated: null,
        });
      }),
    );

    renderBasketConstructor();

    selectSource('Мой портфель');
    fireEvent.click(screen.getByTestId('basket-preset-button'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-11')).toBeInTheDocument());
    expect(requestedSource).toBe('portfolio');
    // estimatedCostRub 10000 / amountRub 15000 * 100 ≈ 66.7%
    const weightInput = screen.getByTestId('basket-draft-weight-11') as HTMLInputElement;
    expect(Number(weightInput.value)).toBeCloseTo(66.67, 1);
  });

  // ─── Задача 35 §C.1: source=market/recommended — instrumentId=null, материализация по secid ────

  it('fills the basket from the greedy preset with source=market, materializing each secid-only candidate', async () => {
    let requestedSource: string | null = null;
    const materializedSecids: string[] = [];
    server.use(
      http.get('*/api/analytics/allocation', ({ request }) => {
        requestedSource = new URL(request.url).searchParams.get('source');
        return HttpResponse.json({
          amountRub: 15000,
          source: 'market',
          allocations: [
            {
              instrumentId: null,
              secid: 'MKT777',
              name: 'Рыночная бумага 777',
              issuer: null,
              sector: 'Корпоративные',
              quantity: 10,
              estimatedCostRub: 10000,
              effectiveYield: 0.2,
              lotSizeAssumed: true,
              cleanCostRub: 9500,
              accruedCostRub: 400,
              commissionCostRub: 100,
              riskSignals: goodSignals,
            },
          ],
          skipped: [],
          leftoverRub: 5000,
          disclaimer: 'аллокация по всему рынку — не рейтинг агентств',
          commissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          candidatePoolAvailable: 12,
          candidatePoolLimit: 50,
          candidatePoolTruncated: false,
        });
      }),
      http.post('*/api/universe/:secid/materialize', ({ params }) => {
        materializedSecids.push(String(params.secid));
        return HttpResponse.json({
          instrumentId: 888,
          secid: params.secid,
          isin: 'RU000MKT7770',
          metrics: { name: 'Рыночная бумага 777', issuer: 'Эмитент рынка' },
          disclaimer: '',
        });
      }),
    );

    renderBasketConstructor();

    // "market" — дефолтный выбор источника, кликать не нужно.
    fireEvent.click(screen.getByTestId('basket-preset-button'));

    // Ключ рендера рыночной строки — secid банк-кандидата (MKT777), НЕ instrumentId=888,
    // полученный от materialize (см. doc-comment handleUseGreedyPreset/эскалация задачи 34).
    await waitFor(() => expect(screen.getByTestId('basket-draft-line-MKT777')).toBeInTheDocument());
    expect(requestedSource).toBe('market');
    expect(materializedSecids).toEqual(['MKT777']);
    expect(screen.getByTestId('basket-draft-line-MKT777').textContent).toMatch(/Рыночная бумага 777/);
    // Вес пресета всё же считается по фактическому instrumentId (весовой ввод/удаление ссылаются
    // на резолвленный instrumentId=888, не на secid) — весовое поле по-прежнему ключуется instrumentId.
    expect(screen.getByTestId('basket-draft-weight-888')).toBeInTheDocument();
    expect(screen.getByTestId('risk-signal-liquidity-draft-MKT777')).toBeInTheDocument();
    // Задача 38 часть C.2: светофор надёжности виден на строке аллокации блока 2 (агрегат goodSignals=Green).
    expect(screen.getByTestId('reliability-dot-draft-MKT777')).toHaveAttribute('data-reliability', 'Green');
    expect(screen.getByTestId('basket-preset-disclaimer').textContent).toMatch(/не рейтинг агентств/);
  });

  it('skips a candidate whose materialize call fails, without crashing the whole preset', async () => {
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({
          amountRub: 15000,
          source: 'market',
          allocations: [
            {
              instrumentId: null,
              secid: 'BAD001',
              name: 'Проблемная бумага',
              issuer: null,
              sector: 'Корпоративные',
              quantity: 5,
              estimatedCostRub: 5000,
              effectiveYield: 0.18,
              lotSizeAssumed: true,
              cleanCostRub: 4800,
              accruedCostRub: 150,
              commissionCostRub: 50,
              riskSignals: goodSignals,
            },
            {
              instrumentId: null,
              secid: 'OK002',
              name: 'Нормальная бумага',
              issuer: null,
              sector: 'Корпоративные',
              quantity: 5,
              estimatedCostRub: 5000,
              effectiveYield: 0.19,
              lotSizeAssumed: true,
              cleanCostRub: 4800,
              accruedCostRub: 150,
              commissionCostRub: 50,
              riskSignals: goodSignals,
            },
          ],
          skipped: [],
          leftoverRub: 5000,
          disclaimer: '',
          commissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          candidatePoolAvailable: 2,
          candidatePoolLimit: 50,
          candidatePoolTruncated: false,
        }),
      ),
      http.post('*/api/universe/:secid/materialize', ({ params }) => {
        if (params.secid === 'BAD001') {
          return HttpResponse.json({ error: 'Бумага не найдена на MOEX', type: 'ValidationException' }, { status: 422 });
        }
        return HttpResponse.json({
          instrumentId: 999,
          secid: params.secid,
          isin: 'RU000OK0020',
          metrics: { name: 'Нормальная бумага', issuer: null },
          disclaimer: '',
        });
      }),
    );

    renderBasketConstructor();

    fireEvent.click(screen.getByTestId('basket-preset-button'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-OK002')).toBeInTheDocument());
    // Строка с ошибкой материализации (BAD001) пропущена, но не роняет применение остальных строк пресета.
    expect(screen.queryByTestId('basket-draft-line-BAD001')).not.toBeInTheDocument();
    expect(screen.getByTestId('basket-preset-error').textContent).toMatch(/1 бумаг/);
  });

  // ─── Задача 35 review (MAJOR 2): кап материализации пресета + прогресс + блокировка контролов ───

  /** Строка аллокации market/recommended с фиксированной формой полей, различается secid/estimatedCostRub. */
  function makeAllocationLine(secid: string, estimatedCostRub: number) {
    return {
      instrumentId: null,
      secid,
      name: `Бумага ${secid}`,
      issuer: null,
      sector: 'Корпоративные',
      quantity: 1,
      estimatedCostRub,
      effectiveYield: 0.18,
      lotSizeAssumed: true,
      cleanCostRub: estimatedCostRub - 50,
      accruedCostRub: 40,
      commissionCostRub: 10,
      riskSignals: goodSignals,
    };
  }

  it('caps the preset materialization batch at 25 rows, keeping the largest by weight, and warns about the rest', async () => {
    // 30 кандидатов пула > лимита (PRESET_MATERIALIZE_LIMIT=25 в BasketConstructor.tsx) — вес
    // (estimatedCostRub) убывает с индексом, топ-25 по весу — MKT001..MKT025.
    const allocations = Array.from({ length: 30 }, (_, i) =>
      makeAllocationLine(`MKT${String(i + 1).padStart(3, '0')}`, (30 - i) * 1000),
    );
    const materializedSecids: string[] = [];
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({
          amountRub: 200000,
          source: 'market',
          allocations,
          skipped: [],
          leftoverRub: 0,
          disclaimer: '',
          commissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          candidatePoolAvailable: 30,
          candidatePoolLimit: 200,
          candidatePoolTruncated: false,
        }),
      ),
      http.post('*/api/universe/:secid/materialize', ({ params }) => {
        const secid = String(params.secid);
        materializedSecids.push(secid);
        return HttpResponse.json({
          instrumentId: Number(secid.replace('MKT', '')),
          secid,
          isin: `RU000${secid}0`,
          metrics: { name: `Бумага ${secid}`, issuer: null },
          disclaimer: '',
        });
      }),
    );

    renderBasketConstructor();

    fireEvent.click(screen.getByTestId('basket-preset-button'));

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-MKT025')).toBeInTheDocument());
    // Только 25 крупнейших по весу строк материализованы — MKT026..MKT030 (наименьший вес) пропущены.
    expect(materializedSecids).toHaveLength(25);
    expect(materializedSecids).not.toContain('MKT026');
    expect(materializedSecids).not.toContain('MKT030');
    expect(screen.queryByTestId('basket-draft-line-MKT026')).not.toBeInTheDocument();
    expect(screen.getByTestId('basket-preset-cap-warning').textContent).toMatch(/25 крупнейших строк пресета из 30/);
  });

  it('shows a materialization progress counter while the preset batch is running', async () => {
    const allocations = [makeAllocationLine('PRG001', 6000), makeAllocationLine('PRG002', 5000)];
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({
          amountRub: 15000,
          source: 'market',
          allocations,
          skipped: [],
          leftoverRub: 0,
          disclaimer: '',
          commissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          candidatePoolAvailable: 2,
          candidatePoolLimit: 200,
          candidatePoolTruncated: false,
        }),
      ),
      http.post('*/api/universe/:secid/materialize', async ({ params }) => {
        // Небольшая задержка имитирует сетевой вызов к MOEX — даёт тесту окно, чтобы поймать
        // промежуточное состояние счётчика прогресса до завершения батча.
        await new Promise((resolve) => setTimeout(resolve, 20));
        const secid = String(params.secid);
        return HttpResponse.json({
          instrumentId: secid === 'PRG001' ? 501 : 502,
          secid,
          isin: `RU000${secid}0`,
          metrics: { name: `Бумага ${secid}`, issuer: null },
          disclaimer: '',
        });
      }),
    );

    renderBasketConstructor();

    fireEvent.click(screen.getByTestId('basket-preset-button'));

    await waitFor(() => expect(screen.getByTestId('basket-preset-progress')).toBeInTheDocument());
    expect(screen.getByTestId('basket-preset-progress').textContent).toMatch(/Материализация \d+ из 2…/);

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-PRG002')).toBeInTheDocument());
    // Счётчик прогресса исчезает по завершении батча вместе с isPresetLoading.
    expect(screen.queryByTestId('basket-preset-progress')).not.toBeInTheDocument();
  });

  it('disables the source control and amount input while the preset materialization batch is loading', async () => {
    const allocations = [makeAllocationLine('DIS001', 6000)];
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({
          amountRub: 15000,
          source: 'market',
          allocations,
          skipped: [],
          leftoverRub: 0,
          disclaimer: '',
          commissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          candidatePoolAvailable: 1,
          candidatePoolLimit: 200,
          candidatePoolTruncated: false,
        }),
      ),
      http.post('*/api/universe/:secid/materialize', async ({ params }) => {
        await new Promise((resolve) => setTimeout(resolve, 20));
        const secid = String(params.secid);
        return HttpResponse.json({
          instrumentId: 601,
          secid,
          isin: 'RU000DIS0010',
          metrics: { name: 'Бумага DIS001', issuer: null },
          disclaimer: '',
        });
      }),
    );

    renderBasketConstructor();

    expect(screen.getByTestId('basket-amount-input')).not.toBeDisabled();

    fireEvent.click(screen.getByTestId('basket-preset-button'));

    await waitFor(() => expect(screen.getByTestId('basket-amount-input')).toBeDisabled());
    within(screen.getByTestId('basket-source-control'))
      .getAllByRole('radio')
      .forEach((radio) => expect(radio).toBeDisabled());

    await waitFor(() => expect(screen.getByTestId('basket-draft-line-DIS001')).toBeInTheDocument());
    expect(screen.getByTestId('basket-amount-input')).not.toBeDisabled();
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
