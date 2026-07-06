import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Recommendations } from './Recommendations';
import { useRecommendationsStore } from '../store/useRecommendationsStore';
import { useWatchlistStore } from '../store/useWatchlistStore';
import { useAuthStore } from '../store/useAuthStore';
import type {
  ComparisonResponse,
  ReplacementMatrixResponse,
  AllocationResponse,
  CompositionResponse,
  WatchlistItem,
} from '../api/types';

function renderRecommendations() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Recommendations />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const baseComposition: CompositionResponse = {
  totalMarketValueRub: 200000,
  byIssuer: [
    { key: 'Слабый Эмитент', marketValueRub: 100000, sharePercent: 50 },
    { key: 'Хороший Эмитент', marketValueRub: 100000, sharePercent: 50 },
  ],
  bySector: [],
  byCouponType: [],
  byDurationBucket: [],
  disclaimer: '',
};

const weakRow = {
  positionId: 1,
  instrumentId: 10,
  name: 'Слабая бумага',
  issuer: 'Слабый Эмитент',
  effectiveYield: 0.08,
  yieldKind: 'Ytm' as const,
  modifiedDuration: 2,
  gSpread: 50,
  daysToHorizon: 30,
  horizonDate: '2026-08-02',
  calculatedToOffer: false,
  couponType: 'Fixed' as const,
  isEstimated: false,
  dataIncomplete: false,
};

const strongRow = {
  positionId: 2,
  instrumentId: 11,
  name: 'Сильная бумага',
  issuer: 'Хороший Эмитент',
  effectiveYield: 0.16,
  yieldKind: 'Ytm' as const,
  modifiedDuration: 2.2,
  gSpread: 150,
  daysToHorizon: 900,
  horizonDate: '2028-12-01',
  calculatedToOffer: false,
  couponType: 'Fixed' as const,
  isEstimated: false,
  dataIncomplete: false,
};

const floaterRow = {
  positionId: 3,
  instrumentId: 12,
  name: 'Флоатер',
  issuer: 'РЖД',
  effectiveYield: 0.1,
  yieldKind: 'Current' as const,
  modifiedDuration: null,
  gSpread: null,
  daysToHorizon: 500,
  horizonDate: '2027-11-01',
  calculatedToOffer: false,
  couponType: 'Floating' as const,
  isEstimated: true,
  dataIncomplete: false,
};

const baseComparison: ComparisonResponse = {
  rows: [weakRow, strongRow, floaterRow],
  disclaimer: 'Сортировка по доходности не учитывает срок до погашения/оферты и кредитный риск эмитента.',
};

const favorableMatrixPair = {
  holdPositionId: 1,
  holdInstrumentId: 10,
  holdName: 'Слабая бумага',
  targetPositionId: 2,
  targetInstrumentId: 11,
  targetName: 'Сильная бумага',
  isWatchlistTarget: false,
  spreadFraction: 0.08,
  capitalRub: 1490,
  horizonYears: 2,
  grossGainRub: 1560,
  sellCommissionRub: 30,
  buyCommissionRub: 30,
  netBenefitRub: 1500,
  annualizedBenefitFraction: 0.21,
  commissionRateUsed: 0.003,
  commissionRateSource: 'Default' as const,
};

const rejectedNotProfitablePair = {
  holdPositionId: 1,
  holdInstrumentId: 10,
  holdName: 'Слабая бумага',
  targetPositionId: 3,
  targetInstrumentId: 13,
  targetName: 'Третья бумага',
  isWatchlistTarget: false,
  reason: 'NotProfitable' as const,
  netBenefitRub: -12,
};

const rejectedDurationMismatchPair = {
  holdPositionId: 1,
  holdInstrumentId: 10,
  holdName: 'Слабая бумага',
  targetPositionId: 4,
  targetInstrumentId: 14,
  targetName: 'Четвёртая бумага',
  isWatchlistTarget: true,
  reason: 'DurationMismatch' as const,
  netBenefitRub: null,
};

const favorableMatrix: ReplacementMatrixResponse = {
  bestPairs: [favorableMatrixPair],
  rejectedPairs: [rejectedNotProfitablePair, rejectedDurationMismatchPair],
  totalConsideredPairs: 3,
  disclaimer: 'Анализ замены сравнивает только текущие позиции портфеля.',
};

const allocationResult: AllocationResponse = {
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
  disclaimer: 'Оценка распределения свободных средств по бумагам текущего портфеля. Не учитывает налоги и не является инвестиционной рекомендацией.',
  commissionRateUsed: 0.003,
  commissionRateSource: 'Default',
};

describe('Recommendations', () => {
  beforeEach(() => {
    useRecommendationsStore.setState({
      sellCandidates: [],
      outOfComparison: [],
      comparisonDisclaimer: '',
      bestPairs: [],
      rejectedPairs: [],
      totalConsideredPairs: 0,
      replacementDisclaimer: '',
      isLoading: false,
      error: null,
      allocationAmount: 15000,
      allocation: null,
      isAllocationLoading: false,
      allocationError: null,
    });
    useWatchlistStore.setState({
      items: [],
      disclaimer: '',
      isLoading: false,
      error: null,
      isAdding: false,
      addError: null,
    });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });

    server.use(
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/comparison', () => HttpResponse.json(baseComparison)),
      http.get('*/api/analytics/replacement-matrix', () => HttpResponse.json(favorableMatrix)),
      http.get('*/api/analytics/allocation', () => HttpResponse.json(allocationResult)),
      http.get('*/api/watchlist', () => HttpResponse.json({ items: [], disclaimer: '' })),
    );
  });

  it('renders all three sections', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.getByTestId('replacements-section')).toBeInTheDocument();
    expect(screen.getByTestId('allocation-section')).toBeInTheDocument();
  });

  it('shows a sell candidate with reason badges', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    const card = screen.getByTestId('sell-candidate-1');
    expect(card.textContent).toMatch(/медианы портфеля/);
    expect(card.textContent).toMatch(/погашение через 30 дн\./);
  });

  it('puts the floater in the "out of comparison" list, not ranked with YTM positions', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('out-of-comparison-3')).toBeInTheDocument());
    expect(screen.queryByTestId('sell-candidate-3')).not.toBeInTheDocument();
  });

  it('shows a favorable replacement card with benefit and annualized percent', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-1-2')).toBeInTheDocument());
    const card = screen.getByTestId('replacement-1-2').textContent!;
    expect(card).toMatch(/выгода/);
    expect(card).toMatch(/21\.00%/);
  });

  // Задача 23 §B.2: карточка раскрывается в построчную формулу (спред → капитал → горизонт →
  // валовая выгода → минус комиссии → чистая выгода ≈ % годовых).
  it('expands the replacement card to show the formula breakdown', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-toggle-1-2')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('replacement-toggle-1-2'));

    await waitFor(() => expect(screen.getByTestId('replacement-details-1-2')).toBeVisible());
    const details = screen.getByTestId('replacement-details-1-2').textContent!;
    expect(details).toMatch(/спред доходностей/);
    expect(details).toMatch(/капитал после продажи/);
    expect(details).toMatch(/горизонт/);
    expect(details).toMatch(/валовая выгода/);
    expect(details).toMatch(/комиссия продажи/);
    expect(details).toMatch(/комиссия покупки/);
    expect(details).toMatch(/чистая выгода/);
  });

  // Plan/22 часть E: карточка замены показывает применённую ставку комиссии и её источник.
  it('shows the commission rate source caption in the expanded formula', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-toggle-1-2')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replacement-toggle-1-2'));

    await waitFor(() => expect(screen.getByTestId('replacement-details-1-2')).toBeVisible());
    expect(screen.getByTestId('replacement-details-1-2').textContent).toMatch(/дефолт 0\.3%/);
  });

  // Задача 23 §B.2: значок watchlist-цели на карточке лучшей пары.
  it('shows a watchlist badge on a replacement card targeting a watchlist bond', async () => {
    server.use(
      http.get('*/api/analytics/replacement-matrix', () =>
        HttpResponse.json({
          bestPairs: [{ ...favorableMatrixPair, targetPositionId: 0, targetInstrumentId: 99, targetName: 'Watchlist bond', isWatchlistTarget: true }],
          rejectedPairs: [],
          totalConsideredPairs: 1,
          disclaimer: favorableMatrix.disclaimer,
        }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-watchlist-badge-1-0')).toBeInTheDocument());
  });

  // Задача 23 §B.3: свёрнутая таблица отвергнутых пар с причинами по-русски.
  it('shows a collapsed table of rejected pairs with reasons in Russian', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('rejected-pairs-toggle')).toBeInTheDocument());
    expect(screen.getByTestId('rejected-pairs-toggle').textContent).toMatch(/2/);

    fireEvent.click(screen.getByTestId('rejected-pairs-toggle'));

    await waitFor(() => expect(screen.getByTestId('rejected-pairs-table')).toBeVisible());
    expect(screen.getByTestId('rejected-pair-1-3').textContent).toMatch(/невыгодна.*-?12/);
    expect(screen.getByTestId('rejected-pair-1-4').textContent).toMatch(/дюрации несопоставимы/);
  });

  // Задача 23 §B.4: пустое состояние показывает число рассмотренных пар из ответа.
  it('shows an empty state with the number of considered pairs when there are no favorable replacements', async () => {
    server.use(
      http.get('*/api/analytics/replacement-matrix', () =>
        HttpResponse.json({ bestPairs: [], rejectedPairs: [], totalConsideredPairs: 7, disclaimer: '' }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacements-empty')).toBeInTheDocument());
    expect(screen.getByTestId('replacements-empty').textContent).toMatch(/7/);
  });

  it('allocation form shows the result and leftover after submit', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-result')).toBeInTheDocument());
    expect(screen.getByTestId('allocation-line-11')).toBeInTheDocument();
    expect(screen.getByTestId('allocation-leftover').textContent).toMatch(/5[\s\u00a0]000/);
  });

  // Plan/22 \u0447\u0430\u0441\u0442\u044c E: \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442 \u0430\u043b\u043b\u043e\u043a\u0430\u0446\u0438\u0438 \u043f\u043e\u043a\u0430\u0437\u044b\u0432\u0430\u0435\u0442 \u0441\u0442\u0430\u0432\u043a\u0443 \u043a\u043e\u043c\u0438\u0441\u0441\u0438\u0438, \u043f\u0440\u0438\u043c\u0435\u043d\u0451\u043d\u043d\u0443\u044e \u043a \u0446\u0435\u043d\u0435 \u043b\u043e\u0442\u0430, \u0438 \u0435\u0451 \u0438\u0441\u0442\u043e\u0447\u043d\u0438\u043a.
  it('shows the commission rate source caption in the allocation result', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-commission-source')).toBeInTheDocument());
    expect(screen.getByTestId('allocation-commission-source').textContent).toMatch(/\u0434\u0435\u0444\u043e\u043b\u0442 0\.3%/);
  });

  it('requests allocation with includeWatchlist=true so watchlist bonds are considered as candidates (plan/20 \u00a7B.2)', async () => {
    let requestedUrl: string | null = null;
    server.use(
      http.get('*/api/analytics/allocation', ({ request }) => {
        requestedUrl = request.url;
        return HttpResponse.json(allocationResult);
      }),
    );

    renderRecommendations();

    await waitFor(() => expect(requestedUrl).not.toBeNull());
    expect(new URL(requestedUrl!).searchParams.get('includeWatchlist')).toBe('true');
  });

  it('renders a watchlist-originated candidate returned by the allocation endpoint', async () => {
    // \u041d\u0430 \u0431\u044d\u043a\u0435 watchlist-\u043a\u0430\u043d\u0434\u0438\u0434\u0430\u0442 \u043d\u0435\u043e\u0442\u043b\u0438\u0447\u0438\u043c \u043f\u043e \u0444\u043e\u0440\u043c\u0435 DTO \u043e\u0442 \u043a\u0430\u043d\u0434\u0438\u0434\u0430\u0442\u0430-\u043f\u043e\u0437\u0438\u0446\u0438\u0438 (\u0442\u043e\u0442 \u0436\u0435 AllocationLineDto) \u2014
    // \u0444\u0440\u043e\u043d\u0442\u0443 \u0434\u043e\u0441\u0442\u0430\u0442\u043e\u0447\u043d\u043e \u043e\u0442\u0440\u0435\u043d\u0434\u0435\u0440\u0438\u0442\u044c \u043b\u044e\u0431\u0443\u044e \u0441\u0442\u0440\u043e\u043a\u0443 \u0438\u0437 \u043e\u0442\u0432\u0435\u0442\u0430; \u0432\u043a\u043b\u044e\u0447\u0451\u043d\u043d\u043e\u0441\u0442\u044c watchlist \u0432 \u043a\u0430\u043d\u0434\u0438\u0434\u0430\u0442\u044b
    // \u043f\u0440\u043e\u0432\u0435\u0440\u044f\u0435\u0442\u0441\u044f \u043f\u0430\u0440\u0430\u043c\u0435\u0442\u0440\u043e\u043c includeWatchlist \u0432 \u043e\u0442\u0434\u0435\u043b\u044c\u043d\u043e\u043c \u0442\u0435\u0441\u0442\u0435 \u0432\u044b\u0448\u0435.
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({
          amountRub: 15000,
          allocations: [
            {
              instrumentId: 77,
              name: '\u0427\u0443\u0436\u0430\u044f \u0431\u0443\u043c\u0430\u0433\u0430 \u0438\u0437 watchlist',
              issuer: '\u0414\u0440\u0443\u0433\u043e\u0439 \u044d\u043c\u0438\u0442\u0435\u043d\u0442',
              quantity: 5,
              estimatedCostRub: 5000,
              effectiveYield: 0.18,
              lotSizeAssumed: true,
            },
          ],
          skipped: [],
          leftoverRub: 10000,
          disclaimer: '',
        }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-line-77')).toBeInTheDocument());
    expect(screen.getByTestId('allocation-line-77').textContent).toMatch(/\u0427\u0443\u0436\u0430\u044f \u0431\u0443\u043c\u0430\u0433\u0430 \u0438\u0437 watchlist/);
  });

  it('renders a disclaimer on the page', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getAllByTestId('disclaimer').length).toBeGreaterThan(0));
  });

  // ─── T-24: разбивка цены лота (чистая цена + НКД + комиссия) ───────────────────────────────

  it('shows the clean price / accrued / commission breakdown in the allocation line', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-line-11')).toBeInTheDocument());
    const line = screen.getByTestId('allocation-line-11');
    // allocationResult: quantity 10, cleanCostRub 9500, accruedCostRub 400, commissionCostRub 100
    // => per bond: цена 1000 (950 clean + 40 accrued + 10 commission).
    expect(line.textContent).toMatch(/950/);
    expect(line.textContent).toMatch(/40/);
    expect(line.textContent).toMatch(/10/);
    expect(line.textContent).toMatch(/НКД/);
    expect(line.textContent).toMatch(/комиссия/);
  });

  it('shows a note that the accrued interest paid on purchase returns with the next coupon', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-accrued-note')).toBeInTheDocument());
    expect(screen.getByTestId('allocation-accrued-note').textContent).toMatch(/вернётся ближайшим купоном/);
  });

  it('shows an empty allocation state when nothing matches the amount', async () => {
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({ amountRub: 100, allocations: [], skipped: [], leftoverRub: 100, disclaimer: '' }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-empty')).toBeInTheDocument());
  });

  it('shows an error state without crashing when comparison fails', async () => {
    server.use(http.get('*/api/analytics/comparison', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-error')).toBeInTheDocument());
  });

  // ─── Задача 20: секция Watchlist ─────────────────────────────────────────────────────────

  const watchlistItem: WatchlistItem = {
    id: 42,
    isin: 'RU000A1038V6',
    note: 'увидел в обзоре',
    addedAtUtc: '2026-06-01T00:00:00Z',
    instrumentId: 99,
    name: 'ОФЗ 26238',
    issuer: 'Минфин РФ',
    sector: 'Гособлигации',
    couponType: 'Fixed',
    maturityDate: '2041-05-15',
    horizonDate: '2041-05-15',
    calculatedToOffer: false,
    modifiedDuration: 5.1,
    macaulayDuration: 5.3,
    ytmEffective: 0.14,
    currentYield: 0.12,
    effectiveYield: 0.14,
    gSpread: 120,
    isFloater: false,
    isIndexed: false,
    isEstimated: false,
    dataIncomplete: false,
  };

  it('renders the watchlist section with existing items', async () => {
    server.use(
      http.get('*/api/watchlist', () =>
        HttpResponse.json({ items: [watchlistItem], disclaimer: 'watchlist disclaimer text' }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('watchlist-row-42')).toBeInTheDocument());
    expect(screen.getByTestId('watchlist-row-42').textContent).toMatch(/ОФЗ 26238/);
    expect(screen.getByTestId('watchlist-row-42').textContent).toMatch(/увидел в обзоре/);
  });

  it('shows an empty watchlist state when there are no items', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('watchlist-empty')).toBeInTheDocument());
  });

  it('adds an ISIN to the watchlist and reloads the list', async () => {
    let postedIsin: string | null = null;
    server.use(
      http.get('*/api/watchlist', () => HttpResponse.json({ items: [], disclaimer: '' })),
      http.post('*/api/watchlist', async ({ request }) => {
        const body = (await request.json()) as { isin: string };
        postedIsin = body.isin;
        return HttpResponse.json(
          { id: 1, isin: body.isin, note: null, addedAtUtc: new Date().toISOString() },
          { status: 201 },
        );
      }),
    );

    renderRecommendations();
    await waitFor(() => expect(screen.getByTestId('watchlist-empty')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('watchlist-isin-input'), { target: { value: 'RU000A1038V6' } });
    fireEvent.click(screen.getByTestId('watchlist-add-button'));

    await waitFor(() => expect(postedIsin).toBe('RU000A1038V6'));
  });

  it('shows a validation error (422) when adding an invalid ISIN, without crashing', async () => {
    server.use(
      http.post('*/api/watchlist', () =>
        HttpResponse.json({ error: 'ISIN не найден на MOEX', type: 'ValidationException' }, { status: 422 }),
      ),
    );

    renderRecommendations();
    await waitFor(() => expect(screen.getByTestId('watchlist-empty')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('watchlist-isin-input'), { target: { value: 'RU000BAD00000' } });
    fireEvent.click(screen.getByTestId('watchlist-add-button'));

    await waitFor(() => expect(screen.getByTestId('watchlist-add-error')).toBeInTheDocument());
    expect(screen.getByTestId('watchlist-add-error').textContent).toMatch(/ISIN не найден/);
  });

  it('removes a watchlist item when the delete button is clicked', async () => {
    let deleteCalled = false;
    server.use(
      http.get('*/api/watchlist', () =>
        HttpResponse.json({ items: [watchlistItem], disclaimer: '' }),
      ),
      http.delete('*/api/watchlist/:id', () => {
        deleteCalled = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderRecommendations();
    await waitFor(() => expect(screen.getByTestId('watchlist-row-42')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('watchlist-remove-42'));

    await waitFor(() => expect(deleteCalled).toBe(true));
    await waitFor(() => expect(screen.queryByTestId('watchlist-row-42')).not.toBeInTheDocument());
  });
});
