import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { PositionDetail } from './PositionDetail';
import { greenFairSignals, reliabilityResponse } from '../test/scorecardFixtures';
import type { PositionDetail as PositionDetailDto } from '../api/types';

function renderDetail(positionId = '1') {
  return render(
    <MantineProvider>
      <MemoryRouter initialEntries={[`/positions/${positionId}`]}>
        <Routes>
          <Route path="/positions/:id" element={<PositionDetail />} />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const baseDetail: PositionDetailDto = {
  positionId: 1,
  instrumentId: 10,
  isin: 'RU000A1038V6',
  name: 'ОФЗ 26238',
  issuer: 'Минфин РФ',
  sector: 'ОФЗ',
  quantity: 100,
  faceValue: 1000,
  currency: 'RUB',
  couponType: 'Fixed',
  maturityDate: '2030-01-01',
  horizonDate: '2030-01-01',
  calculatedToOffer: false,
  hasAmortization: false,
  hasOffers: false,
  cleanPrice: 1000,
  accruedInterest: 12.5,
  accruedTotalRub: 1250,
  dirtyPrice: 1012.5,
  marketValueRub: 101250,
  ytmEffective: 0.125,
  ytmSimple: 0.12,
  currentYield: 0.118,
  macaulayDuration: 3.4,
  modifiedDuration: 3.2,
  convexity: 15.2,
  pvbp: 32.4,
  gSpread: 0.005,
  isFloater: false,
  isIndexed: false,
  isEstimated: false,
  dataIncomplete: false,
  isOutOfScopeCurrency: false,
  notes: [],
  averageCostRub: 980,
  investedRub: 98000,
  unrealizedPnlRub: 3250,
  unrealizedPnlPercent: 0.0332,
  couponsReceivedRub: 3500,
  totalReturnPercent: 0.0687,
  costBasisIncomplete: false,
  priceHistory: [
    { date: '2026-06-01', closePricePercent: 99.5, accruedInterestRub: 10 },
    { date: '2026-06-15', closePricePercent: 100.2, accruedInterestRub: 12 },
    { date: '2026-07-01', closePricePercent: 101.25, accruedInterestRub: 12.5 },
  ],
  couponSchedule: [
    { couponDate: '2026-01-15', valueRub: 40, valueForPositionRub: 4000, isKnown: true, isPast: true },
    { couponDate: '2026-09-15', valueRub: 40, valueForPositionRub: 4000, isKnown: true, isPast: false },
  ],
  amortizationSchedule: [],
  offerSchedule: [],
  operations: [
    { id: 1, type: 'Buy', date: '2026-01-01T00:00:00Z', amountRub: -98000, quantity: 100 },
    { id: 2, type: 'Coupon', date: '2026-01-15T00:00:00Z', amountRub: 3500, quantity: null },
  ],
  ifSoldNow: {
    marketValueRub: 101250,
    cleanValueRub: 100000,
    accruedTotalRub: 1250,
    commissionRub: 303.75,
    commissionRate: 0.003,
    commissionRateSource: 'Default',
    netProceedsRub: 100946.25,
    realizedPnlRub: 2946.25,
    realizedPnlPercent: 0.0301,
    couponsReceivedRub: 3500,
    totalReturnWithCouponsRub: 6446.25,
    pnlAvailable: true,
    taxEstimateRub: 383.01,
    netAfterTaxRub: 6063.24,
    disclaimer: 'Оценочный расчёт, налог оценён приблизительно.',
  },
  disclaimer: 'Тестовый дисклеймер карточки позиции.',
};

describe('PositionDetail', () => {
  beforeEach(() => {
  });

  it('renders all sections from the mocked response', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('position-detail-header')).toBeInTheDocument());
    expect(screen.getByText('ОФЗ 26238')).toBeInTheDocument();
    expect(screen.getByTestId('price-history-chart')).toBeInTheDocument();
    expect(screen.getByTestId('position-detail-metrics')).toBeInTheDocument();
    expect(screen.getByTestId('instrument-calendar')).toBeInTheDocument();
    expect(screen.getByTestId('position-operations')).toBeInTheDocument();
    expect(screen.getByTestId('if-sold-now-card')).toBeInTheDocument();
    expect(screen.getByText('Тестовый дисклеймер карточки позиции.')).toBeInTheDocument();
  });

  it('shows the если-продать-сейчас net proceeds and total return', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('if-sold-now-net-proceeds')).toBeInTheDocument());
    expect(screen.getByTestId('if-sold-now-net-proceeds')).toHaveTextContent('100');
  });

  // ─── строка налога и итог после налога в если-продать-сейчас ─────────────────────────

  it('shows the tax estimate row and the after-tax total when tax is available', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('if-sold-now-tax-estimate')).toBeInTheDocument());
    expect(screen.getByTestId('if-sold-now-tax-estimate').textContent).toMatch(/383/);

    const netAfterTax = screen.getByTestId('if-sold-now-net-after-tax');
    expect(netAfterTax.textContent).toMatch(/6[\s]063/);

    expect(screen.getByTestId('if-sold-now-pretax-caption').textContent).toMatch(/6[\s]446/);
  });

  it('shows "налог не оценён" captions when the tax cannot be estimated (incomplete journal)', async () => {
    server.use(
      http.get('*/api/positions/1', () =>
        HttpResponse.json({
          ...baseDetail,
          ifSoldNow: { ...baseDetail.ifSoldNow, taxEstimateRub: null, netAfterTaxRub: null },
        }),
      ),
    );

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('if-sold-now-tax-unavailable')).toBeInTheDocument());
    expect(screen.getByTestId('if-sold-now-tax-unavailable').textContent).toMatch(/журнал операций неполон/);
    expect(screen.getByTestId('if-sold-now-net-after-tax-unavailable').textContent).toMatch(/журнал операций неполон/);
    expect(screen.queryByTestId('if-sold-now-tax-estimate')).not.toBeInTheDocument();
    expect(screen.queryByTestId('if-sold-now-net-after-tax')).not.toBeInTheDocument();
  });

  // ─── разложение выручки на чистую стоимость + НКД − комиссию ────────────────────────

  it('shows the accrued caption and the "если продать" formula when accruedTotalRub is positive', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('if-sold-now-accrued-caption')).toBeInTheDocument());
    expect(screen.getByTestId('if-sold-now-accrued-caption')).toHaveTextContent('1 250');

    const formula = screen.getByTestId('if-sold-now-formula');
    expect(formula).toHaveTextContent('чистая стоимость');
    expect(formula).toHaveTextContent('100 000'); // cleanValueRub
    expect(formula).toHaveTextContent('НКД');
    expect(formula).toHaveTextContent('1 250'); // accruedTotalRub
    expect(formula).toHaveTextContent('комиссия');
  });

  it('hides the accrued caption and formula when accruedTotalRub is zero', async () => {
    server.use(
      http.get('*/api/positions/1', () =>
        HttpResponse.json({
          ...baseDetail,
          ifSoldNow: { ...baseDetail.ifSoldNow, accruedTotalRub: 0, cleanValueRub: baseDetail.ifSoldNow.marketValueRub },
        }),
      ),
    );

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('if-sold-now-net-proceeds')).toBeInTheDocument());
    expect(screen.queryByTestId('if-sold-now-accrued-caption')).not.toBeInTheDocument();
    expect(screen.queryByTestId('if-sold-now-formula')).not.toBeInTheDocument();
  });

  // Подпись с применённой ставкой комиссии и её источником.
  it('shows the commission rate source caption in the если-продать-сейчас card', async () => {
    server.use(
      http.get('*/api/positions/1', () =>
        HttpResponse.json({
          ...baseDetail,
          ifSoldNow: { ...baseDetail.ifSoldNow, commissionRateSource: 'EstimatedFromTrades' },
        }),
      ),
    );

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('if-sold-now-commission-source')).toBeInTheDocument());
    expect(screen.getByTestId('if-sold-now-commission-source')).toHaveTextContent('из ваших сделок');
  });

  it('shows past and future rows in the instrument calendar', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    await waitFor(() => expect(screen.getAllByTestId('calendar-row-past').length).toBeGreaterThan(0));
    expect(screen.getAllByTestId('calendar-row-future').length).toBeGreaterThan(0);
  });

  it('shows an empty-state placeholder for the price chart without crashing when priceHistory is empty', async () => {
    server.use(
      http.get('*/api/positions/1', () => HttpResponse.json({ ...baseDetail, priceHistory: [] })),
    );

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('price-history-empty')).toBeInTheDocument());
    // Остальная страница по-прежнему рендерится, не падает.
    expect(screen.getByTestId('position-detail-metrics')).toBeInTheDocument();
  });

  it('re-requests price history when the range toggle changes', async () => {
    let lastRangeQuery: string | null = null;
    server.use(
      http.get('*/api/positions/1', ({ request }) => {
        const url = new URL(request.url);
        lastRangeQuery = url.searchParams.get('range');
        return HttpResponse.json(baseDetail);
      }),
    );

    renderDetail();
    await waitFor(() => expect(lastRangeQuery).toBe('6m'));

    const { default: userEvent } = await import('@testing-library/user-event');
    const oneYearButton = screen.getByRole('radio', { name: '1г' });
    await userEvent.click(oneYearButton);

    await waitFor(() => expect(lastRangeQuery).toBe('1y'));
  });

  it('shows an error state without crashing when the request fails', async () => {
    server.use(
      http.get('*/api/positions/1', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
    );

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('position-detail-error')).toBeInTheDocument());
  });

  it('renders a back-to-positions link', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('back-to-positions')).toBeInTheDocument());
  });

  // ─── Задача 47: секция «Надёжность» (развёрнутая скоркарта + ручной ввод эмитента) ──────

  it('renders the expanded scorecard in the "Надёжность" section and prefills the issuer INN for manual input', async () => {
    server.use(
      http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)),
      http.get('*/api/universe/:isin/reliability', ({ params }) => {
        expect(params.isin).toBe(baseDetail.isin);
        return HttpResponse.json(reliabilityResponse({ emitentInn: '7707083893' }));
      }),
    );

    renderDetail();

    const section = await screen.findByTestId('position-reliability');
    const card = await within(section).findByTestId('reliability-score-card-position');
    expect(within(card).getByTestId('score-card-score-position')).toHaveTextContent('42');
    expect(within(card).getByTestId('score-component-interest_coverage-position')).toBeInTheDocument();
    expect(within(card).getByTestId('score-card-coverage-position')).toHaveTextContent('данных: 78%');
    expect(await within(section).findByTestId('issuer-inn-input')).toHaveValue('7707083893');
  });

  it('refetches the scorecard after a manual rating is saved, so the new score is shown immediately', async () => {
    let reliabilityCalls = 0;
    server.use(
      http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)),
      http.get('*/api/universe/:isin/reliability', () => {
        reliabilityCalls += 1;
        return HttpResponse.json(
          reliabilityResponse({ riskSignals: reliabilityCalls === 1 ? reliabilityResponse().riskSignals : greenFairSignals }),
        );
      }),
      http.post('*/api/issuers/:inn/ratings', async ({ request }) => HttpResponse.json(await request.json())),
    );

    renderDetail();

    await waitFor(() => expect(screen.getByTestId('score-card-score-position')).toHaveTextContent('42'));
    fireEvent.click(await screen.findByTestId('issuer-ratings-toggle'));
    fireEvent.change(await screen.findByTestId('issuer-rating-value'), { target: { value: 'AA' } });
    fireEvent.change(screen.getByTestId('issuer-rating-date'), { target: { value: new Date().toISOString().slice(0, 10) } });
    fireEvent.click(screen.getByTestId('issuer-rating-save'));

    await waitFor(() => expect(screen.getByTestId('score-card-score-position')).toHaveTextContent('88'));
    expect(reliabilityCalls).toBe(2);
  });

  it('keeps the page working when the bond is not in the bank (scorecard 404): error note, INN entered manually', async () => {
    server.use(http.get('*/api/positions/1', () => HttpResponse.json(baseDetail)));

    renderDetail();

    const section = await screen.findByTestId('position-reliability');
    expect(await within(section).findByTestId('position-reliability-error')).toBeInTheDocument();
    expect(within(section).getByTestId('issuer-inn-input')).toHaveValue('');
    expect(screen.getByTestId('position-detail-metrics')).toBeInTheDocument();
  });
});
