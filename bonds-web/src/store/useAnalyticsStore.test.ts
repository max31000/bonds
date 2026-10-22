import { describe, expect, it } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { useAnalyticsStore } from './useAnalyticsStore';

const composition = (total: number) => ({
  totalMarketValueRub: total,
  byIssuer: [],
  bySector: [],
  byCouponType: [],
  byDurationBucket: [],
  disclaimer: '',
});

describe('useAnalyticsStore.loadFundScope — защита от гонки', () => {
  it('медленный ответ старого режима не перезаписывает новый', async () => {
    server.use(
      http.get('*/api/analytics/composition', async ({ request }) => {
        const withFunds = new URL(request.url).searchParams.get('includeFunds') !== 'false';
        if (withFunds) {
          await delay(60);
          return HttpResponse.json(composition(150_000));
        }
        return HttpResponse.json(composition(100_000));
      }),
    );

    const first = useAnalyticsStore.getState().loadFundScope(true);
    const second = useAnalyticsStore.getState().loadFundScope(false);
    await Promise.all([first, second]);

    expect(useAnalyticsStore.getState().composition?.totalMarketValueRub).toBe(100_000);
  });
});
