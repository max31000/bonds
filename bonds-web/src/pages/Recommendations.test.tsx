import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
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
  CompositionResponse,
  ReplacementCandidatesResponse,
  RiskSignals,
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
  yieldKind: 'CurrentYield' as const,
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

const goodSignals: RiskSignals = {
  liquidity: 'Good',
  liquidityLabel: 'Высокая ликвидность, листинг 1',
  spread: 'Neutral',
  gSpreadFraction: 0.03,
  spreadVsBasketMedianFraction: 0.001,
};

// Задача 37 часть D: weakRow.modifiedDuration = 2 — окно фильтра «похожая дюрация» ±1.5 года даёт
// [0.5, 3.5]. MKT001 (1.5) и MKT002 (3.4) — внутри окна, MKT003 (6) — вне, MKT004 (null) — без
// дюрации (скрывается при включённом фильтре независимо от окна).
const marketCandidatesResponse: ReplacementCandidatesResponse = {
  mode: 'market',
  positionIsin: 'RU000WEAK001',
  candidates: [
    {
      secid: 'MKT001',
      isin: 'RU000MKT0001',
      name: 'Рыночная бумага 1',
      issuer: null,
      sector: 'Корпоративные',
      yieldFraction: 0.22,
      durationYears: 1.5,
      gSpreadFraction: 0.04,
      offerDate: null,
      riskSignals: goodSignals,
    },
    {
      secid: 'MKT002',
      isin: 'RU000MKT0002',
      name: 'Рыночная бумага 2',
      issuer: null,
      sector: 'Корпоративные',
      yieldFraction: 0.2,
      durationYears: 3.4,
      gSpreadFraction: 0.035,
      offerDate: '2027-05-01',
      riskSignals: goodSignals,
    },
    {
      secid: 'MKT003',
      isin: 'RU000MKT0003',
      name: 'Рыночная бумага 3 (далёкая дюрация)',
      issuer: null,
      sector: 'Корпоративные',
      yieldFraction: 0.19,
      durationYears: 6,
      gSpreadFraction: 0.03,
      offerDate: null,
      riskSignals: goodSignals,
    },
    {
      secid: 'MKT004',
      isin: 'RU000MKT0004',
      name: 'Рыночная бумага 4 (без дюрации)',
      issuer: null,
      sector: 'Корпоративные',
      yieldFraction: 0.18,
      durationYears: null,
      gSpreadFraction: 0.028,
      offerDate: null,
      riskSignals: goodSignals,
    },
  ],
  disclaimer: 'Кандидаты и оценки — аналитическая информация, не рейтинг рейтинговых агентств.',
};

const rvCandidatesResponse: ReplacementCandidatesResponse = {
  mode: 'rv',
  positionIsin: 'RU000WEAK001',
  candidates: [
    {
      secid: 'RVCAND1',
      isin: 'RU000RVCAND1',
      name: 'RV-сосед 1',
      issuer: null,
      sector: 'Корпоративные',
      yieldFraction: 0.2,
      durationYears: 1.8,
      gSpreadFraction: 0.045,
      offerDate: null,
      riskSignals: { ...goodSignals, spread: 'Caution' },
    },
  ],
  disclaimer: marketCandidatesResponse.disclaimer,
};

/** Мок POST /universe/{secid}/materialize + POST /analytics/replacement для любого secid — используется
 * тестами задачи 37, где выбирается не только MKT001. */
function mockReplacementFlow() {
  server.use(
    http.post('*/api/universe/:secid/materialize', ({ params }) => {
      const secid = params.secid as string;
      return HttpResponse.json({
        instrumentId: 777,
        secid,
        isin: `RU000${secid}`,
        metrics: {
          name: `Материализовано ${secid}`,
          issuer: 'Эмитент рынка',
          sector: 'Корпоративные',
          couponType: 'Fixed',
          maturityDate: '2029-01-01',
          horizonDate: '2029-01-01',
          calculatedToOffer: false,
          modifiedDuration: 1.5,
          macaulayDuration: 1.55,
          ytmEffective: 0.22,
          currentYield: 0.2,
          effectiveYield: 0.22,
          gSpread: 400,
          isFloater: false,
          isIndexed: false,
          isEstimated: false,
          dataIncomplete: false,
        },
        disclaimer: '',
      });
    }),
    http.post('*/api/analytics/replacement', () =>
      HttpResponse.json({
        holdPositionId: 1,
        targetPositionId: 0,
        targetInstrumentId: 777,
        horizonYears: 2,
        sellCommissionRub: 30,
        buyCommissionRub: 30,
        totalSwitchCostRub: 60,
        netBenefitRub: 1400,
        isSwitchFavorable: true,
        breakEvenYears: 0.4,
        yieldDataIncomplete: false,
        disclaimer: '',
        sellCommissionRateUsed: 0.003,
        buyCommissionRateUsed: 0.003,
        commissionRateSource: 'Default',
        spreadFraction: 0.09,
        capitalRub: 1490,
        grossGainRub: 1460,
        annualizedBenefitFraction: 0.19,
        sellTaxEstimateRub: null,
        netBenefitAfterTaxRub: null,
        targetRiskSignals: goodSignals,
      }),
    ),
  );
}

describe('Recommendations', () => {
  beforeEach(() => {
    useRecommendationsStore.setState({
      sellCandidates: [],
      outOfComparison: [],
      comparisonDisclaimer: '',
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
      http.get('*/api/analytics/relative-value', () => HttpResponse.json({ positions: [], disclaimer: '' })),
      http.get('*/api/analytics/replacement-candidates', ({ request }) => {
        const url = new URL(request.url);
        const mode = url.searchParams.get('mode');
        return HttpResponse.json(mode === 'rv' ? rvCandidatesResponse : marketCandidatesResponse);
      }),
      http.get('*/api/universe*', () => HttpResponse.json({ rows: [], total: 0, hiddenCount: 0 })),
      http.get('*/api/watchlist', () => HttpResponse.json({ items: [], disclaimer: '' })),
    );
  });

  // ─── Задача 35: страница = два главных блока ────────────────────────────────────────────────

  it('renders block 1 (weak positions) and block 2 (basket constructor), with a top disclaimer', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.getByTestId('basket-constructor')).toBeInTheDocument();
    expect(screen.getByTestId('page-disclaimer')).toBeInTheDocument();
  });

  it('does not render the old replacement-matrix or standalone RV sections', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.queryByTestId('replacements-section')).not.toBeInTheDocument();
    expect(screen.queryByTestId('relative-value-section')).not.toBeInTheDocument();
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

  it('shows an error state without crashing when comparison fails', async () => {
    server.use(http.get('*/api/analytics/comparison', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-error')).toBeInTheDocument());
  });

  // ─── Задача 35 §B: подбор замены на карточке слабой позиции ────────────────────────────────

  it('opens the replace panel and defaults to the "доходные рынка" mode, fetching candidates with risk badges', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    expect(screen.queryByTestId('replace-panel-1')).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('replace-toggle-1'));

    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeVisible());
    const row = screen.getByTestId('replace-candidate-MKT001');
    expect(within(row).getByTestId('risk-signal-liquidity-MKT001')).toBeInTheDocument();
    expect(within(row).getByTestId('risk-signal-spread-MKT001')).toBeInTheDocument();
  });

  it('switches to "дешёвые соседи (RV)" mode and requests mode=rv', async () => {
    let lastMode: string | null = null;
    server.use(
      http.get('*/api/analytics/replacement-candidates', ({ request }) => {
        lastMode = new URL(request.url).searchParams.get('mode');
        return HttpResponse.json(lastMode === 'rv' ? rvCandidatesResponse : marketCandidatesResponse);
      }),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(lastMode).toBe('market'));

    fireEvent.click(within(screen.getByTestId('replace-mode-1')).getByText('дешёвые соседи (RV)'));

    await waitFor(() => expect(lastMode).toBe('rv'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-RVCAND1')).toBeInTheDocument());
  });

  it('switches to "поиск" mode and reveals the existing MarketComparator', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument());

    fireEvent.click(within(screen.getByTestId('replace-mode-1')).getByText('поиск'));

    await waitFor(() => expect(screen.getByTestId('market-comparator-1')).toBeInTheDocument());
  });

  it('selecting a market candidate materializes it, requests the benefit, and shows the ReplacementBreakdown', async () => {
    server.use(
      http.post('*/api/universe/:secid/materialize', () =>
        HttpResponse.json({
          instrumentId: 777,
          secid: 'MKT001',
          isin: 'RU000MKT0001',
          metrics: {
            name: 'Рыночная бумага 1',
            issuer: 'Эмитент рынка',
            sector: 'Корпоративные',
            couponType: 'Fixed',
            maturityDate: '2029-01-01',
            horizonDate: '2029-01-01',
            calculatedToOffer: false,
            modifiedDuration: 1.5,
            macaulayDuration: 1.55,
            ytmEffective: 0.22,
            currentYield: 0.2,
            effectiveYield: 0.22,
            gSpread: 400,
            isFloater: false,
            isIndexed: false,
            isEstimated: false,
            dataIncomplete: false,
          },
          disclaimer: '',
        }),
      ),
      http.post('*/api/analytics/replacement', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        expect(body.targetInstrumentId).toBe(777);
        expect(body.holdPositionId).toBe(1);
        return HttpResponse.json({
          holdPositionId: 1,
          targetPositionId: 0,
          targetInstrumentId: 777,
          horizonYears: 2,
          sellCommissionRub: 30,
          buyCommissionRub: 30,
          totalSwitchCostRub: 60,
          netBenefitRub: 1400,
          isSwitchFavorable: true,
          breakEvenYears: 0.4,
          yieldDataIncomplete: false,
          disclaimer: '',
          sellCommissionRateUsed: 0.003,
          buyCommissionRateUsed: 0.003,
          commissionRateSource: 'Default',
          spreadFraction: 0.09,
          capitalRub: 1490,
          grossGainRub: 1460,
          annualizedBenefitFraction: 0.19,
          sellTaxEstimateRub: null,
          netBenefitAfterTaxRub: null,
          targetRiskSignals: goodSignals,
        });
      }),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('replace-candidate-MKT001'));

    await waitFor(() => expect(screen.getByTestId('replace-result-1')).toBeInTheDocument());
    const result = screen.getByTestId('replace-result-1');
    expect(within(result).getByTestId('replace-benefit-1').textContent).toMatch(/выгода/);
    expect(within(result).getByTestId('replacement-details-candidate-1')).toBeInTheDocument();
    expect(within(result).getByTestId('risk-signal-liquidity-replace-1')).toBeInTheDocument();
  });

  // ─── Задача 37: карточка выгоды под выбранной строкой + фильтр «похожая дюрация» + дата оферты ──

  it('renders the benefit card directly under the selected row, not after the whole list', async () => {
    mockReplacementFlow();
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT002')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('replace-candidate-MKT002'));

    await waitFor(() => expect(screen.getByTestId('replace-result-1')).toBeVisible());
    const row = screen.getByTestId('replace-candidate-MKT002');
    const wrapper = row.parentElement as HTMLElement;
    expect(within(wrapper).getByTestId('replace-result-1')).toBeInTheDocument();
  });

  it('collapses the benefit card on a second click of the same row', async () => {
    mockReplacementFlow();
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('replace-candidate-MKT001'));
    await waitFor(() => expect(screen.getByTestId('replace-result-1')).toBeVisible());

    fireEvent.click(screen.getByTestId('replace-candidate-MKT001'));
    await waitFor(() => expect(screen.queryByTestId('replace-result-1')).not.toBeInTheDocument());
  });

  it('moves the benefit card under a different row when a different candidate is clicked', async () => {
    mockReplacementFlow();
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('replace-candidate-MKT001'));
    await waitFor(() => expect(screen.getByTestId('replace-result-1')).toBeVisible());
    const firstWrapper = screen.getByTestId('replace-candidate-MKT001').parentElement as HTMLElement;
    expect(within(firstWrapper).getByTestId('replace-result-1')).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('replace-candidate-MKT002'));
    await waitFor(() => expect(screen.getByTestId('replace-result-1')).toBeVisible());
    const secondWrapper = screen.getByTestId('replace-candidate-MKT002').parentElement as HTMLElement;
    expect(within(secondWrapper).getByTestId('replace-result-1')).toBeInTheDocument();
    expect(within(firstWrapper).queryByTestId('replace-result-1')).not.toBeInTheDocument();
  });

  it('shows the offer-date badge when offerDate is present and hides it when null', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));

    await waitFor(() => expect(screen.getByTestId('replace-candidate-offer-MKT002')).toBeInTheDocument());
    expect(screen.getByTestId('replace-candidate-offer-MKT002').textContent).toMatch(/оферта 01\.05\.2027/);
    expect(screen.queryByTestId('replace-candidate-offer-MKT001')).not.toBeInTheDocument();
  });

  it('filters candidates to the ±1.5-year duration window around the position and shows an honest "N of M" count', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument());

    // Дефолт — фильтр выключен, весь список из 4 кандидатов виден, счётчика нет.
    expect(screen.getByTestId('replace-candidate-MKT003')).toBeInTheDocument();
    expect(screen.getByTestId('replace-candidate-MKT004')).toBeInTheDocument();
    expect(screen.queryByTestId('replace-duration-filter-count-1')).not.toBeInTheDocument();

    // weakRow.modifiedDuration=2, окно ±1.5 -> [0.5, 3.5]: MKT001(1.5)/MKT002(3.4) внутри,
    // MKT003(6) снаружи, MKT004(null) скрыт независимо от окна.
    fireEvent.click(screen.getByTestId('replace-duration-filter-1'));

    await waitFor(() => expect(screen.getByTestId('replace-duration-filter-count-1').textContent).toMatch(/2 из 4/));
    expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument();
    expect(screen.getByTestId('replace-candidate-MKT002')).toBeInTheDocument();
    expect(screen.queryByTestId('replace-candidate-MKT003')).not.toBeInTheDocument();
    expect(screen.queryByTestId('replace-candidate-MKT004')).not.toBeInTheDocument();
  });

  it('shows a friendly empty state when the duration filter leaves no candidates', async () => {
    server.use(
      http.get('*/api/analytics/replacement-candidates', ({ request }) => {
        const mode = new URL(request.url).searchParams.get('mode');
        if (mode === 'rv') return HttpResponse.json(rvCandidatesResponse);
        // Только MKT003 (дюрация 6, далеко от позиции с дюрацией 2) — под фильтром список опустеет.
        return HttpResponse.json({ ...marketCandidatesResponse, candidates: [marketCandidatesResponse.candidates[2]] });
      }),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT003')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('replace-duration-filter-1'));

    await waitFor(() => expect(screen.getByTestId('replace-duration-filter-empty-1')).toBeInTheDocument());
    expect(screen.getByTestId('replace-duration-filter-empty-1').textContent).toMatch(/отключите фильтр/);
    expect(screen.queryByTestId('replace-candidate-MKT003')).not.toBeInTheDocument();
  });

  it('does not break the weak-position card when GET /api/analytics/replacement-candidates fails', async () => {
    server.use(http.get('*/api/analytics/replacement-candidates', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));

    await waitFor(() => expect(screen.getByTestId('replace-candidates-error-1')).toBeInTheDocument());
    // Карточка/страница не падают — переключатель режимов остаётся в DOM.
    expect(screen.getByTestId('replace-mode-1')).toBeInTheDocument();
  });

  it('shows an empty state for mode=rv when there are no cheap-basket-neighbor candidates', async () => {
    server.use(
      http.get('*/api/analytics/replacement-candidates', ({ request }) => {
        const mode = new URL(request.url).searchParams.get('mode');
        if (mode === 'rv') return HttpResponse.json({ mode: 'rv', positionIsin: '', candidates: [], disclaimer: '' });
        return HttpResponse.json(marketCandidatesResponse);
      }),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('replace-toggle-1'));
    await waitFor(() => expect(screen.getByTestId('replace-candidate-MKT001')).toBeInTheDocument());

    fireEvent.click(within(screen.getByTestId('replace-mode-1')).getByText('дешёвые соседи (RV)'));

    await waitFor(() => expect(screen.getByTestId('replace-candidates-empty-1')).toBeInTheDocument());
  });

  it('renders a disclaimer on the page', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getAllByTestId('disclaimer').length).toBeGreaterThan(0));
  });

  // ─── Задача 30: RV-бейдж на карточке слабой позиции (переработан задачей 35, но переиспользуется) ──

  const richRelativeValue: RelativeValueResponse = {
    positions: [
      {
        positionId: 1,
        basket: { sector: 'Корпоративные', durationBucket: '1–3 года', count: 7, confidence: 'High' },
        deviationFraction: -0.0038,
        percentile: 8,
        verdict: 'Rich',
        basedOnDays: 5,
        cheapCandidates: [],
      },
    ],
    disclaimer: 'относительная дешевизна к корзине по данным MOEX; НЕ оценка кредитного качества',
  };

  it('shows an RV badge on a weak-position card with a Rich verdict', async () => {
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json(richRelativeValue)));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('relative-value-badge-1')).toBeInTheDocument());
    expect(screen.getByTestId('relative-value-badge-1').textContent).toMatch(/дорогая/);
  });

  it('does not break the recommendations page when GET /api/analytics/relative-value fails', async () => {
    server.use(http.get('*/api/analytics/relative-value', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.getByTestId('basket-constructor')).toBeInTheDocument();
    expect(screen.queryByTestId('relative-value-badge-1')).not.toBeInTheDocument();
  });

  // ─── Задача 35 §C: Блок 2 — переключатель источника кандидатов аллокации ────────────────────

  it('renders the basket constructor with a source switcher for the greedy preset', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('basket-source-control')).toBeInTheDocument());
    expect(screen.getByText('Весь рынок — доходные')).toBeInTheDocument();
    expect(screen.getByText('Рекомендованные')).toBeInTheDocument();
    expect(screen.getByText('Мой портфель')).toBeInTheDocument();
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
