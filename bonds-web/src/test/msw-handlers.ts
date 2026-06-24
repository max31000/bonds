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
];

export const server = setupServer(...handlers);
