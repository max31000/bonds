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
import { useRelativeValueStore } from '../store/useRelativeValueStore';
import type {
  ComparisonResponse,
  ReplacementMatrixResponse,
  CompositionResponse,
  WatchlistItem,
  RelativeValueResponse,
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
  sellTaxEstimateRub: 200,
  netBenefitAfterTaxRub: 1300,
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
    useRelativeValueStore.setState({ positionsById: {}, disclaimer: '', isLoading: false, error: null, hasLoaded: false });

    server.use(
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/comparison', () => HttpResponse.json(baseComparison)),
      http.get('*/api/analytics/replacement-matrix', () => HttpResponse.json(favorableMatrix)),
      http.get('*/api/analytics/relative-value', () => HttpResponse.json({ positions: [], disclaimer: '' })),
      http.get('*/api/universe*', () => HttpResponse.json({ rows: [], total: 0, hiddenCount: 0 })),
      http.get('*/api/watchlist', () => HttpResponse.json({ items: [], disclaimer: '' })),
    );
  });

  it('renders the weak-links, replacements, and basket constructor sections', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.getByTestId('replacements-section')).toBeInTheDocument();
    expect(screen.getByTestId('basket-constructor')).toBeInTheDocument();
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

  // ─── Задача 27: MarketComparator на карточке слабой позиции ────────────────────────────────

  it('reveals the MarketComparator when "сравнить с рынком" is clicked on a weak-link card', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    expect(screen.queryByTestId('market-comparator-1')).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('compare-with-market-toggle-1'));

    await waitFor(() => expect(screen.getByTestId('market-comparator-1')).toBeVisible());
  });

  it('shows a favorable replacement card with benefit and annualized percent', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-1-2')).toBeInTheDocument());
    const card = screen.getByTestId('replacement-1-2').textContent!;
    expect(card).toMatch(/выгода/);
    expect(card).toMatch(/21\.00%/);
  });

  // ─── T-25: выгода после налога в заголовке карточки и в раскрывашке формулы ────────────────

  it('shows the after-tax benefit in the headline when sellTaxEstimateRub is available', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-benefit-1-2')).toBeInTheDocument());
    const headline = screen.getByTestId('replacement-benefit-1-2').textContent!;
    expect(headline).toMatch(/после налога/);
    expect(headline).toMatch(/1[\s ]300/); // netBenefitAfterTaxRub
  });

  it('shows the sell tax row and after-tax net line in the expanded formula', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-toggle-1-2')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replacement-toggle-1-2'));

    await waitFor(() => expect(screen.getByTestId('replacement-details-1-2')).toBeVisible());
    const taxRow = screen.getByTestId('replacement-sell-tax-1-2').textContent!;
    expect(taxRow).toMatch(/НДФЛ от продажи/);
    expect(taxRow).toMatch(/200/);

    const netAfterTax = screen.getByTestId('replacement-net-after-tax-1-2').textContent!;
    expect(netAfterTax).toMatch(/1[\s ]300/);
  });

  it('shows "налог не оценён" caption when sellTaxEstimateRub is null (incomplete journal)', async () => {
    server.use(
      http.get('*/api/analytics/replacement-matrix', () =>
        HttpResponse.json({
          bestPairs: [{ ...favorableMatrixPair, sellTaxEstimateRub: null, netBenefitAfterTaxRub: null }],
          rejectedPairs: [],
          totalConsideredPairs: 1,
          disclaimer: favorableMatrix.disclaimer,
        }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-benefit-1-2')).toBeInTheDocument());
    expect(screen.getByTestId('replacement-benefit-1-2').textContent).not.toMatch(/после налога/);

    fireEvent.click(screen.getByTestId('replacement-toggle-1-2'));

    await waitFor(() => expect(screen.getByTestId('replacement-tax-unavailable-1-2')).toBeInTheDocument());
    expect(screen.getByTestId('replacement-tax-unavailable-1-2').textContent).toMatch(/журнал операций.*неполон/);
    expect(screen.queryByTestId('replacement-sell-tax-1-2')).not.toBeInTheDocument();
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

  it('renders a disclaimer on the page', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getAllByTestId('disclaimer').length).toBeGreaterThan(0));
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

  // ─── Задача 30: relative value (RV) — бейджи, секция «дешёвых соседей», устойчивость к отказу ──

  const richRelativeValue: RelativeValueResponse = {
    positions: [
      {
        positionId: 1,
        basket: { sector: 'Корпоративные', durationBucket: '1–3 года', count: 7, confidence: 'High' },
        deviationFraction: -0.0038,
        percentile: 8,
        verdict: 'Rich',
        basedOnDays: 5,
        cheapCandidates: [
          { secid: 'CHEAP1', name: 'Дешёвая бумага 1', yieldFraction: 0.22, deviationFraction: 0.004, liquidityScore: 'High' },
          { secid: 'CHEAP2', name: 'Дешёвая бумага 2', yieldFraction: 0.24, deviationFraction: 0.003, liquidityScore: 'Medium' },
        ],
      },
      {
        positionId: 2,
        basket: { sector: 'Корпоративные', durationBucket: '1–3 года', count: 7, confidence: 'High' },
        deviationFraction: 0.0025,
        percentile: 78,
        verdict: 'Cheap',
        basedOnDays: 5,
        cheapCandidates: [],
      },
    ],
    disclaimer: 'относительная дешевизна к корзине по данным MOEX; НЕ оценка кредитного качества',
  };

  it('shows an RV badge on a weak-link card with a Rich verdict', async () => {
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json(richRelativeValue)));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('relative-value-badge-1')).toBeInTheDocument());
    expect(screen.getByTestId('relative-value-badge-1').textContent).toMatch(/дорогая/);
  });

  it('shows an RV badge with a Cheap verdict on a different position', async () => {
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json(richRelativeValue)));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('relative-value-badge-2')).toBeInTheDocument());
    expect(screen.getByTestId('relative-value-badge-2').textContent).toMatch(/дешёвая/);
  });

  it('renders the "дорогие бумаги — дешёвые соседи" section with cheap candidate chips for a Rich position', async () => {
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json(richRelativeValue)));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('relative-value-section')).toBeInTheDocument());
    expect(screen.getByTestId('rv-rich-row-1')).toBeInTheDocument();
    expect(screen.getByTestId('rv-candidate-chip-CHEAP1')).toBeInTheDocument();
    expect(screen.getByTestId('rv-candidate-chip-CHEAP2')).toBeInTheDocument();
    // Cheap-позиция (positionId=2) не должна получить свою строку в секции — только Rich.
    expect(screen.queryByTestId('rv-rich-row-2')).not.toBeInTheDocument();
  });

  it('clicking a cheap candidate chip opens MarketComparator preselected with that bond', async () => {
    server.use(
      http.get('*/api/analytics/relative-value', () => HttpResponse.json(richRelativeValue)),
      http.get('*/api/universe*', ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('search') === 'CHEAP1') {
          return HttpResponse.json({
            rows: [
              {
                secid: 'CHEAP1',
                isin: 'RU000CHEAP01',
                name: 'Дешёвая бумага 1',
                sector: 'Корпоративные',
                yieldFraction: 0.22,
                durationYears: 2,
                pricePercent: 98,
                turnoverRub: 2_000_000,
                listLevel: 1,
                liquidityScore: 'High',
                slippageEstimateFraction: 0.001,
                gspreadApproxFraction: 0.05,
                maturityDate: '2028-01-01',
                offerDate: null,
                isHidden: false,
                hiddenReason: null,
                inPortfolio: false,
                inWatchlist: false,
              },
            ],
            total: 1,
            hiddenCount: 0,
            disclaimer: '',
          });
        }
        return HttpResponse.json({ rows: [], total: 0, hiddenCount: 0, disclaimer: '' });
      }),
      http.post('*/api/universe/:secid/materialize', () =>
        HttpResponse.json({
          instrumentId: 999,
          secid: 'CHEAP1',
          isin: 'RU000CHEAP01',
          metrics: {
            name: 'Дешёвая бумага 1',
            issuer: 'Эмитент',
            sector: 'Корпоративные',
            couponType: 'Fixed',
            maturityDate: '2028-01-01',
            horizonDate: '2028-01-01',
            calculatedToOffer: false,
            modifiedDuration: 2,
            macaulayDuration: 2.05,
            ytmEffective: 0.22,
            currentYield: 0.2,
            effectiveYield: 0.22,
            gSpread: 500,
            isFloater: false,
            isIndexed: false,
            isEstimated: false,
            dataIncomplete: false,
          },
          disclaimer: '',
        }),
      ),
      http.post('*/api/analytics/replacement', () =>
        HttpResponse.json({
          holdPositionId: 1,
          targetPositionId: 0,
          targetInstrumentId: 999,
          horizonYears: 2,
          sellCommissionRub: 30,
          buyCommissionRub: 30,
          totalSwitchCostRub: 60,
          netBenefitRub: 1200,
          isSwitchFavorable: true,
          breakEvenYears: 0.4,
          yieldDataIncomplete: false,
          disclaimer: '',
          sellCommissionRateUsed: 0.003,
          buyCommissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          spreadFraction: 0.07,
          capitalRub: 1490,
          grossGainRub: 1260,
          annualizedBenefitFraction: 0.18,
          sellTaxEstimateRub: null,
          netBenefitAfterTaxRub: null,
        }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('rv-candidate-chip-CHEAP1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('rv-candidate-chip-CHEAP1'));

    await waitFor(() => expect(screen.getByTestId('rv-market-comparator-1')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
  });

  it('shows the young-history caption in the RV badge tooltip when basedOnDays is 0', async () => {
    const youngHistory: RelativeValueResponse = {
      positions: [{ ...richRelativeValue.positions[0], basedOnDays: 0 }],
      disclaimer: richRelativeValue.disclaimer,
    };
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json(youngHistory)));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('relative-value-badge-1')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.hover(screen.getByTestId('relative-value-badge-1'));

    await waitFor(() => expect(screen.getByText(/истории пока мало/)).toBeInTheDocument());
  });

  it('does not break the recommendations page when GET /api/analytics/relative-value fails', async () => {
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.getByTestId('replacements-section')).toBeInTheDocument();
    expect(screen.queryByTestId('relative-value-badge-1')).not.toBeInTheDocument();
    expect(screen.queryByTestId('relative-value-section')).not.toBeInTheDocument();
  });
});
