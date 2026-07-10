import { render, screen, waitFor, fireEvent, within, act } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { MarketComparator } from './MarketComparator';
import { useAuthStore } from '../store/useAuthStore';
import type { UniverseRow, MaterializeResponse, ReplacementResponse } from '../api/types';

function renderComparator(holdPositionId = 1) {
  return render(
    <MantineProvider>
      <MarketComparator holdPositionId={holdPositionId} />
    </MantineProvider>,
  );
}

const gazpromRow: UniverseRow = {
  secid: 'RU000GAZP01',
  isin: 'RU000GAZP001',
  name: 'Газпром капитал 5',
  sector: 'Корпоративные',
  yieldFraction: 0.19,
  durationYears: 1.8,
  pricePercent: 97,
  turnoverRub: 5_000_000,
  listLevel: 1,
  liquidityScore: 'High',
  slippageEstimateFraction: 0.001,
  gspreadApproxFraction: 0.03,
  maturityDate: '2028-01-01',
  offerDate: null,
  isHidden: false,
  hiddenReason: null,
  inPortfolio: false,
  inWatchlist: false,
};

// Задача 37 часть C.2: бумага с офертой — бейдж «оферта {дата}» должен появиться и в опции
// выпадашки, и на карточке результата (доходность к оферте — другой горизонт, чем к погашению).
const offerRow: UniverseRow = {
  ...gazpromRow,
  secid: 'RU000OFFER01',
  isin: 'RU000OFFER001',
  name: 'Бумага с офертой',
  offerDate: '2027-09-15',
};

const lowLiquidityRow: UniverseRow = {
  ...gazpromRow,
  secid: 'RU000LOWLIQ',
  isin: 'RU000LOWLIQ1',
  name: 'Малоликвидный выпуск',
  liquidityScore: 'Low',
  slippageEstimateFraction: 0.02,
};

const top10Rows: UniverseRow[] = [
  gazpromRow,
  { ...gazpromRow, secid: 'RU000OTHER1', isin: 'RU000OTHER01', name: 'Другая бумага', yieldFraction: 0.15 },
];

const floaterRow: UniverseRow = {
  ...gazpromRow,
  secid: 'RU000FLOAT01',
  isin: 'RU000FLOAT001',
  name: 'Флоатер РЖД',
  yieldFraction: 0.25,
  isFloater: true,
};

const materializeResponse: MaterializeResponse = {
  instrumentId: 555,
  secid: gazpromRow.secid,
  isin: gazpromRow.isin!,
  metrics: {
    name: gazpromRow.name,
    issuer: 'Газпром',
    sector: gazpromRow.sector,
    couponType: 'Fixed',
    maturityDate: '2028-01-01',
    horizonDate: '2028-01-01',
    calculatedToOffer: false,
    modifiedDuration: 1.8,
    macaulayDuration: 1.85,
    ytmEffective: 0.19,
    currentYield: 0.17,
    effectiveYield: 0.19,
    gSpread: 300,
    isFloater: false,
    isIndexed: false,
    isEstimated: false,
    dataIncomplete: false,
  },
  disclaimer: 'watchlist disclaimer text',
};

const replacementResponse: ReplacementResponse = {
  holdPositionId: 1,
  targetPositionId: 0,
  targetInstrumentId: 555,
  horizonYears: 2,
  sellCommissionRub: 30,
  buyCommissionRub: 30,
  totalSwitchCostRub: 60,
  netBenefitRub: 1500,
  isSwitchFavorable: true,
  breakEvenYears: 0.5,
  yieldDataIncomplete: false,
  disclaimer: 'Анализ замены сравнивает только текущие позиции портфеля.',
  sellCommissionRateUsed: 0.003,
  buyCommissionRateUsed: 0.003,
  commissionRateSource: 'Default',
  spreadFraction: 0.08,
  capitalRub: 1490,
  grossGainRub: 1560,
  annualizedBenefitFraction: 0.21,
  sellTaxEstimateRub: 200,
  netBenefitAfterTaxRub: 1300,
};

describe('MarketComparator', () => {
  beforeEach(() => {
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
  });

  it('shows top-10 by yield by default when the search input is empty', async () => {
    let requestedUrl: string | null = null;
    server.use(
      http.get('*/api/universe', ({ request }) => {
        requestedUrl = request.url;
        return HttpResponse.json({ rows: top10Rows, total: 2, hiddenCount: 0, disclaimer: '' });
      }),
    );

    renderComparator();

    await waitFor(() => expect(requestedUrl).not.toBeNull());
    const url = new URL(requestedUrl!);
    expect(url.searchParams.get('sortBy')).toBe('yield');
    expect(url.searchParams.get('sortDir')).toBe('desc');
    // Задача 32 часть C: 20, не 10 — запас на клиентский отсев флоатеров (см. MarketComparator.tsx).
    expect(url.searchParams.get('limit')).toBe('20');
    expect(url.searchParams.get('search')).toBeNull();
  });

  it('sends a search query with the typed text after debounce', async () => {
    const requestedSearches: (string | null)[] = [];
    server.use(
      http.get('*/api/universe', ({ request }) => {
        requestedSearches.push(new URL(request.url).searchParams.get('search'));
        return HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' });
      }),
    );

    renderComparator();
    await waitFor(() => expect(requestedSearches.length).toBeGreaterThan(0));

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.change(input, { target: { value: 'газпром' } });

    await waitFor(() => expect(requestedSearches).toContain('газпром'), { timeout: 1000 });
  });

  it('selecting a bond materializes it, requests replacement, and shows the formula card', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () => HttpResponse.json(materializeResponse)),
      http.post('*/api/analytics/replacement', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        expect(body.targetInstrumentId).toBe(555);
        expect(body.holdPositionId).toBe(1);
        return HttpResponse.json(replacementResponse);
      }),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
    const result = screen.getByTestId('market-comparator-result-1');
    expect(within(result).getByTestId('market-comparator-benefit-1').textContent).toMatch(/выгода/);
    expect(within(result).getByTestId('market-comparator-benefit-1').textContent).toMatch(/после налога/);
    expect(within(result).getByTestId('replacement-details-market-1')).toBeInTheDocument();
    expect(within(result).getByTestId('replacement-details-market-1').textContent).toMatch(/спред доходностей/);
  });

  // ─── Задача 32 часть C: флоатеры-цели исключены из выпадашки сравнивалки ─────────────────

  it('excludes floaters from the target dropdown options — only fixed-coupon bonds are selectable', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow, floaterRow], total: 2, hiddenCount: 0, disclaimer: '' })),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    expect(screen.queryByText(floaterRow.name!)).not.toBeInTheDocument();
  });

  it('shows a friendly message and does not crash when the server returns 422 for a floater/indexed target', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () => HttpResponse.json(materializeResponse)),
      http.post('*/api/analytics/replacement', () =>
        HttpResponse.json(
          {
            error:
              'Бумага с плавающим/индексируемым купоном несравнима по доходности с фикс-купонной бумагой — выберите фикс-купонную бумагу для сравнения.',
            type: 'ValidationException',
          },
          { status: 422 },
        ),
      ),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-error-1')).toBeInTheDocument());
    expect(screen.getByTestId('market-comparator-error-1').textContent).toMatch(/несравнима по доходности/);
    // Компонент не падает — селект и остальная разметка остаются в DOM.
    expect(screen.getByTestId('market-comparator-select-1')).toBeInTheDocument();
  });

  it('shows the 422 error text from materialize without crashing', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () =>
        HttpResponse.json({ error: 'Бумага не найдена на MOEX', type: 'ValidationException' }, { status: 422 }),
      ),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-error-1')).toBeInTheDocument());
    expect(screen.getByTestId('market-comparator-error-1').textContent).toMatch(/не найдена на MOEX/);
  });

  it('shows a low-liquidity warning on the result card', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [lowLiquidityRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () =>
        HttpResponse.json({ ...materializeResponse, secid: lowLiquidityRow.secid, isin: lowLiquidityRow.isin }),
      ),
      http.post('*/api/analytics/replacement', () => HttpResponse.json(replacementResponse)),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(lowLiquidityRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(lowLiquidityRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-liquidity-warning-1')).toBeInTheDocument());
    expect(screen.getByTestId('market-comparator-liquidity-warning-1').textContent).toMatch(/Низкая ликвидность/);
  });

  // ─── Задача 37 часть C.2: бейдж «оферта {дата}» в опции и на карточке результата ────────────

  it('shows the offer-date badge on the dropdown option and the result card when offerDate is present', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [offerRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () =>
        HttpResponse.json({ ...materializeResponse, secid: offerRow.secid, isin: offerRow.isin }),
      ),
      http.post('*/api/analytics/replacement', () => HttpResponse.json(replacementResponse)),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByTestId(`market-comparator-option-offer-${offerRow.secid}`)).toBeInTheDocument());
    expect(screen.getByTestId(`market-comparator-option-offer-${offerRow.secid}`).textContent).toMatch(/оферта 15\.09\.2027/);

    fireEvent.click(screen.getByText(offerRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
    expect(screen.getByTestId('market-comparator-result-offer-1').textContent).toMatch(/оферта 15\.09\.2027/);
  });

  it('does not show the offer-date badge when offerDate is null', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () => HttpResponse.json(materializeResponse)),
      http.post('*/api/analytics/replacement', () => HttpResponse.json(replacementResponse)),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    expect(screen.queryByTestId(`market-comparator-option-offer-${gazpromRow.secid}`)).not.toBeInTheDocument();

    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
    expect(screen.queryByTestId('market-comparator-result-offer-1')).not.toBeInTheDocument();
  });

  // ─── Задача 33 часть A.4 / 35 §B.3: риск-сигналы таргета из ответа POST /replacement ──────────

  it('shows risk-signal badges on the result card when the replacement response carries targetRiskSignals', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () => HttpResponse.json(materializeResponse)),
      http.post('*/api/analytics/replacement', () =>
        HttpResponse.json({
          ...replacementResponse,
          targetRiskSignals: {
            liquidity: 'Good',
            liquidityLabel: 'Высокая ликвидность, листинг 1',
            spread: 'Caution',
            gSpreadFraction: 0.05,
            spreadVsBasketMedianFraction: 0.012,
          },
        }),
      ),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
    expect(screen.getByTestId('risk-signal-liquidity-market-1')).toBeInTheDocument();
    expect(screen.getByTestId('risk-signal-spread-market-1')).toBeInTheDocument();
  });

  it('does not render risk-signal badges when targetRiskSignals is absent (target not found in the bank)', async () => {
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () => HttpResponse.json(materializeResponse)),
      http.post('*/api/analytics/replacement', () => HttpResponse.json(replacementResponse)),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
    expect(screen.queryByTestId('risk-signal-liquidity-market-1')).not.toBeInTheDocument();
  });

  it('clicking "В watchlist" posts the materialized ISIN to the watchlist endpoint', async () => {
    let postedIsin: string | null = null;
    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: [gazpromRow], total: 1, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', () => HttpResponse.json(materializeResponse)),
      http.post('*/api/analytics/replacement', () => HttpResponse.json(replacementResponse)),
      http.post('*/api/watchlist', async ({ request }) => {
        const body = (await request.json()) as { isin: string };
        postedIsin = body.isin;
        return HttpResponse.json({ id: 1, isin: body.isin, note: null, addedAtUtc: new Date().toISOString() }, { status: 201 });
      }),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());
    fireEvent.click(screen.getByText(gazpromRow.name!));

    await waitFor(() => expect(screen.getByTestId('market-comparator-watchlist-button-1')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('market-comparator-watchlist-button-1'));

    await waitFor(() => expect(postedIsin).toBe(gazpromRow.isin));
  });

  // ─── T-37 fix (ревью): гонка запросов при быстром переключении выбора в выпадашке ───────────

  /** Промис с внешним resolve — управляет моментом резолва materialize/replacement (тест гонки). */
  function createDeferred<T = void>() {
    let resolve!: (value: T | PromiseLike<T>) => void;
    const promise = new Promise<T>((res) => {
      resolve = res;
    });
    return { promise, resolve: () => resolve(undefined as T) };
  }

  it('does not let a stale response for the first-selected bond overwrite the card after a second bond was selected and resolved', async () => {
    const otherSecid = top10Rows[1].secid; // 'RU000OTHER1' — «Другая бумага»
    const materializeDeferreds: Record<string, ReturnType<typeof createDeferred>> = {
      [gazpromRow.secid]: createDeferred(),
      [otherSecid]: createDeferred(),
    };
    const replacementDeferreds: Record<number, ReturnType<typeof createDeferred>> = {
      555: createDeferred(), // materialize возвращает instrumentId=555 для газпрома
      777: createDeferred(), // и 777 для «Другая бумага»
    };

    server.use(
      http.get('*/api/universe', () => HttpResponse.json({ rows: top10Rows, total: 2, hiddenCount: 0, disclaimer: '' })),
      http.post('*/api/universe/:secid/materialize', async ({ params }) => {
        const secid = params.secid as string;
        const instrumentId = secid === gazpromRow.secid ? 555 : 777;
        await materializeDeferreds[secid].promise;
        return HttpResponse.json({
          ...materializeResponse,
          instrumentId,
          secid,
          isin: `RU000${secid}`,
          metrics: { ...materializeResponse.metrics, name: `Материализовано ${secid}` },
        });
      }),
      http.post('*/api/analytics/replacement', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        const targetInstrumentId = body.targetInstrumentId as number;
        await replacementDeferreds[targetInstrumentId].promise;
        return HttpResponse.json({ ...replacementResponse, targetInstrumentId, netBenefitRub: targetInstrumentId });
      }),
    );

    renderComparator();

    const input = screen.getByTestId('market-comparator-select-1');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });
    await waitFor(() => expect(screen.getByText(gazpromRow.name!)).toBeInTheDocument());

    // Выбор первой бумаги (газпром) — запрос стартует и зависает на deferred.
    fireEvent.click(screen.getByText(gazpromRow.name!));

    // До резолва первой — открываем выпадашку заново и выбираем другую бумагу.
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: '' } });
    await waitFor(() => expect(screen.getByText('Другая бумага')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Другая бумага'));

    // Вторая бумага резолвится первой — карточка должна показать именно её данные.
    materializeDeferreds[otherSecid].resolve();
    replacementDeferreds[777].resolve();
    await waitFor(() => expect(screen.getByTestId('market-comparator-result-1')).toBeInTheDocument());
    expect(screen.getByTestId('market-comparator-result-1').textContent).toMatch(`Материализовано ${otherSecid}`);

    // Первая (газпром) резолвится позже — не должна перезаписать уже показанную карточку.
    materializeDeferreds[gazpromRow.secid].resolve();
    replacementDeferreds[555].resolve();
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
      await new Promise((r) => setTimeout(r, 0));
    });

    expect(screen.getByTestId('market-comparator-result-1').textContent).toMatch(`Материализовано ${otherSecid}`);
    expect(screen.getByTestId('market-comparator-result-1').textContent).not.toMatch(`Материализовано ${gazpromRow.secid}`);
  });
});
