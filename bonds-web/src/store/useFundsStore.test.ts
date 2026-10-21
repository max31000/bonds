import { describe, expect, it } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { baseFund, baseFunds } from '../test/fundsFixtures';
import { useFundsStore } from './useFundsStore';

describe('useFundsStore.load — защита от гонки', () => {
  it('устаревший (медленный) ответ не перезаписывает более свежий', async () => {
    let call = 0;
    server.use(
      http.get('*/api/funds', async () => {
        const n = ++call;
        if (n === 1) {
          await delay(60);
          return HttpResponse.json({ ...baseFunds, funds: [{ ...baseFund, name: 'СТАРЫЙ' }] });
        }
        return HttpResponse.json({ ...baseFunds, funds: [{ ...baseFund, name: 'НОВЫЙ' }] });
      }),
    );

    const first = useFundsStore.getState().load();
    const second = useFundsStore.getState().load();
    await Promise.all([first, second]);

    expect(useFundsStore.getState().funds[0].name).toBe('НОВЫЙ');
    expect(useFundsStore.getState().isLoading).toBe(false);
  });
});
