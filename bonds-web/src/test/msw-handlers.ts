import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';

export const handlers = [
  http.post('*/api/auth/telegram', () =>
    HttpResponse.json({
      token: 'mock-jwt-token',
      user: { id: 1, telegramId: 123456789, firstName: 'Owner' },
    }),
  ),
  http.get('*/api/auth/me', () =>
    HttpResponse.json({ id: 1, telegramId: 123456789, firstName: 'Owner' }),
  ),
  // Дефолтный кейс — пустой портфель; конкретные сценарии (обычная бумага/флоатер/
  // dataIncomplete/401) переопределяются через server.use(...) в тестах экрана позиций.
  http.get('*/api/positions', () =>
    HttpResponse.json({ positions: [], disclaimer: '' }),
  ),
  // Дефолтный кейс для 19 — конкретные сценарии переопределяются в тестах карточки позиции.
  http.get('*/api/positions/:id', () =>
    HttpResponse.json({ error: 'not found' }, { status: 404 }),
  ),
  // Дефолтные кейсы для 09b — пустые данные; конкретные сценарии переопределяются в тестах экранов.
  http.get('*/api/cashflow', () =>
    HttpResponse.json({ byMonth: [], byPosition: [], principalReleases: [], disclaimer: '' }),
  ),
  http.get('*/api/analytics/scatter', () =>
    HttpResponse.json({ points: [], curve: [], curveAsOf: null, disclaimer: '' }),
  ),
  http.get('*/api/analytics/composition', () =>
    HttpResponse.json({
      totalMarketValueRub: 0,
      byIssuer: [],
      bySector: [],
      byCouponType: [],
      byDurationBucket: [],
      disclaimer: '',
    }),
  ),
  http.get('*/api/analytics/xirr', () =>
    HttpResponse.json({ currentXirr: null, history: [], disclaimer: '' }),
  ),
  http.get('*/api/analytics/rate-scenario', () =>
    HttpResponse.json({ currentValueRub: 0, scenarios: [], disclaimer: '' }),
  ),
  http.get('*/api/analytics/trajectory', () =>
    HttpResponse.json({ initialValueRub: 0, withReinvest: [], withoutReinvest: [], reinvestRateUsed: 0, disclaimer: '' }),
  ),
  // Дефолтные кейсы для 17 — пустые данные; конкретные сценарии переопределяются в тестах экрана рекомендаций.
  http.get('*/api/analytics/comparison', () => HttpResponse.json({ rows: [], disclaimer: '' })),
  http.post('*/api/analytics/replacement', () =>
    HttpResponse.json({
      holdPositionId: 0,
      targetPositionId: 0,
      horizonYears: 2,
      sellCommissionRub: 0,
      buyCommissionRub: 0,
      totalSwitchCostRub: 0,
      netBenefitRub: 0,
      isSwitchFavorable: false,
      breakEvenYears: null,
      yieldDataIncomplete: true,
      disclaimer: '',
    }),
  ),
  http.get('*/api/analytics/allocation', () =>
    HttpResponse.json({ amountRub: 15000, allocations: [], skipped: [], leftoverRub: 15000, disclaimer: '' }),
  ),
  // Дефолтный кейс для 29 (BasketConstructor) — конкретные сценарии переопределяются в тестах компонента.
  http.post('*/api/analytics/basket', () =>
    HttpResponse.json({
      basket: {
        amountRub: 0,
        lines: [],
        leftoverRub: 0,
        metrics: { totalCostRub: 0, weightedYield: null, weightedDuration: null, hasExcludedFloaters: false },
      },
      whatIf: {
        before: { totalValueRub: 0, weightedYield: null, weightedDuration: null, hasExcludedFloaters: false },
        after: { totalValueRub: 0, weightedYield: null, weightedDuration: null, hasExcludedFloaters: false },
        concentrations: [],
        warnings: [],
      },
      disclaimer: '',
    }),
  ),
  // Дефолтные кейсы для 20 — пустой watchlist; конкретные сценарии переопределяются в тестах.
  http.get('*/api/watchlist', () => HttpResponse.json({ items: [], disclaimer: '' })),
  http.post('*/api/watchlist', () =>
    HttpResponse.json({ id: 1, isin: 'RU000A1038V6', note: null, addedAtUtc: new Date().toISOString() }, { status: 201 }),
  ),
  http.delete('*/api/watchlist/:id', () => new HttpResponse(null, { status: 204 })),
  // Дефолтные кейсы для 27 (MarketComparator — выпадашка-сравнивалка) — пустой банк по умолчанию;
  // конкретные сценарии переопределяются в тестах MarketComparator/Recommendations/Screener.
  http.get('*/api/universe', () => HttpResponse.json({ rows: [], total: 0, hiddenCount: 0, disclaimer: '' })),
  // Дефолтный кейс для 28 (страница «Скринер», статусная строка) — переопределяется в Screener.test.tsx.
  http.get('*/api/universe/status', () =>
    HttpResponse.json({ lastRefreshUtc: null, totalBonds: 0, hiddenBonds: 0, historyDays: 0 }),
  ),
  // Дефолтные кейсы для 09c — пустые/неактивные данные; переопределяются в тестах экранов.
  http.get('*/api/signals', () => HttpResponse.json({ signals: [] })),
  http.post('*/api/signals/:id/read', ({ params }) =>
    HttpResponse.json({ id: Number(params.id), isRead: true }),
  ),
  http.post('*/api/sync', () =>
    HttpResponse.json({
      alreadyRunning: false,
      noAccountConfigured: false,
      instrumentsSynced: 0,
      operationsUpserted: 0,
      yieldCurveUpdated: false,
      positionsProjected: 0,
      flowsWritten: 0,
      snapshotStored: false,
      signalsCreated: 0,
      errors: [],
      hasErrors: false,
    }),
  ),
  http.get('*/api/sync/status', () =>
    HttpResponse.json({
      isRunning: false,
      lastRunStartedAtUtc: null,
      lastSuccessAtUtc: null,
      lastFailureAtUtc: null,
      lastRunErrors: [],
    }),
  ),
  http.get('*/api/settings', () =>
    HttpResponse.json({
      baseCurrency: 'RUB',
      tInvestTokenConfigured: false,
      tInvestTokenMasked: null,
      upcomingEventDaysThreshold: 14,
      uninvestedCashThresholdRub: 10000,
      uninvestedCashLookbackDays: 7,
      yieldBelowAlternativeBpsThreshold: 50,
      maturityWindowDaysForAlternativeComparison: 30,
      defaultMaxConcentrationPercent: 25,
      durationDriftToleranceYears: 0.5,
    }),
  ),
  http.put('*/api/settings', async ({ request }) => {
    const body = (await request.json()) as Record<string, unknown>;
    return HttpResponse.json({
      baseCurrency: 'RUB',
      tInvestTokenConfigured: false,
      tInvestTokenMasked: null,
      ...body,
    });
  }),
  http.put('*/api/settings/tinvest-token', () =>
    HttpResponse.json({ tInvestTokenConfigured: true, tInvestTokenMasked: '...1234' }),
  ),
  // Дефолтные кейсы для plan/16 — пустые данные; конкретные сценарии переопределяются в тестах.
  http.get('*/api/live/positions', () =>
    HttpResponse.json({ positions: [], totalMarketValueRub: 0, asOfUtc: new Date().toISOString() }),
  ),
  http.get('*/api/live/portfolio-intraday', () => HttpResponse.json({ points: [] })),
];

export const server = setupServer(...handlers);
