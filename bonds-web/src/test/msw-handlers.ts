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
];

export const server = setupServer(...handlers);
