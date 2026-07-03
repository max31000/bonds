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
import type { ComparisonResponse, ReplacementResponse, AllocationResponse, CompositionResponse, WatchlistItem } from '../api/types';

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

const favorableReplacement: ReplacementResponse = {
  holdPositionId: 1,
  targetPositionId: 2,
  horizonYears: 2,
  sellCommissionRub: 30,
  buyCommissionRub: 30,
  totalSwitchCostRub: 60,
  netBenefitRub: 1500,
  isSwitchFavorable: true,
  breakEvenYears: 0.2,
  yieldDataIncomplete: false,
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
    },
  ],
  skipped: [],
  leftoverRub: 5000,
  disclaimer: 'Оценка распределения свободных средств по бумагам текущего портфеля. Не учитывает налоги и не является инвестиционной рекомендацией.',
};

describe('Recommendations', () => {
  beforeEach(() => {
    useRecommendationsStore.setState({
      sellCandidates: [],
      outOfComparison: [],
      comparisonDisclaimer: '',
      replacements: [],
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
      http.post('*/api/analytics/replacement', () => HttpResponse.json(favorableReplacement)),
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

  it('shows a favorable replacement card with benefit and break-even', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-1-2')).toBeInTheDocument());
    expect(screen.getByTestId('replacement-1-2').textContent).toMatch(/выгода/);
  });

  it('allocation form shows the result and leftover after submit', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-result')).toBeInTheDocument());
    expect(screen.getByTestId('allocation-line-11')).toBeInTheDocument();
    expect(screen.getByTestId('allocation-leftover').textContent).toMatch(/5[\s\u00a0]000/);
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
