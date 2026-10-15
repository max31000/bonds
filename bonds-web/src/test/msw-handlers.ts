import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';

/** Ответ `/auth/api/me` для владельца (контракт кита: `bonds` обязан быть в `apps`). */
export const OWNER_ME = {
  user: {
    id: 42,
    telegramId: 123456789,
    name: 'Owner',
    username: 'owner',
    photoUrl: null,
    isAdmin: true,
    isOwner: true,
  },
  apps: [{ id: 'bonds', anonymous: false, access: 'admin', granted: true }],
};

export const handlers = [
  // Общий mvv42-auth: владелец с доступом к bonds; refresh сессии в тестах не удаётся (финальный 401).
  http.get('*/auth/api/me', () => HttpResponse.json(OWNER_ME)),
  http.post('*/auth/api/refresh', () => HttpResponse.json({ error: 'no_session' }, { status: 401 })),
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
    HttpResponse.json({ byMonth: [], byPosition: [], principalReleases: [], nextPayments: [], disclaimer: '' }),
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
    HttpResponse.json({
      amountRub: 15000,
      source: 'portfolio',
      allocations: [],
      skipped: [],
      leftoverRub: 15000,
      disclaimer: '',
      commissionRateUsed: 0.003,
      commissionRateSource: 'Default',
      candidatePoolAvailable: null,
      candidatePoolLimit: null,
      candidatePoolTruncated: null,
    }),
  ),
  // Дефолтный кейс для 33/35 (панель подбора замены, блок 1) — пусто; конкретные сценарии
  // переопределяются в тестах Recommendations.
  http.get('*/api/analytics/replacement-candidates', () =>
    HttpResponse.json({ mode: 'market', positionIsin: '', candidates: [], disclaimer: '' }),
  ),
  // Дефолтный кейс для 30 (relative value) — пусто; конкретные сценарии переопределяются в тестах
  // Recommendations/Positions.
  http.get('*/api/analytics/relative-value', () => HttpResponse.json({ positions: [], disclaimer: '' })),
  // Задача 49: вердикты позиций — по умолчанию пусто (бейджи не рисуются); сценарии переопределяются в тестах.
  http.get('*/api/analytics/position-verdicts', () => HttpResponse.json({ verdicts: [], disclaimer: '' })),
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
  // Задача 47: скоркарта бумаги и ручной ввод данных эмитента — по умолчанию «бумаги нет в банке» и пустые списки;
  // конкретные сценарии переопределяются через server.use в тестах.
  http.get('*/api/universe/:secid/reliability', () => HttpResponse.json({ error: 'not found' }, { status: 404 })),
  http.get('*/api/issuers/:inn/ratings', ({ params }) =>
    HttpResponse.json({ inn: params.inn, ratings: [], freshMonths: 18, disclaimer: '' }),
  ),
  http.get('*/api/issuers/:inn/financials', ({ params }) =>
    HttpResponse.json({ inn: params.inn, girBoStatus: null, years: [], disclaimer: '' }),
  ),
  // Дефолтные кейсы для 09c — пустые/неактивные данные; переопределяются в тестах экранов.
  http.get('*/api/signals', () => HttpResponse.json({ signals: [] })),
  http.post('*/api/signals/read-all', () => HttpResponse.json({ acknowledged: 0 })),
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
      defaultMaxConcentrationPercent: 25,
      replacementHorizonYears: 2,
      spreadWideningThresholdFraction: 0.005,
      priceDrawdownThresholdFraction: 0.05,
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
