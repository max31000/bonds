import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import userEvent from '@testing-library/user-event';
import { server } from '../test/msw-handlers';
import { baseFund, baseFundDetail } from '../test/fundsFixtures';
import { FundDetail } from './FundDetail';
import { useFundsStore } from '../store/useFundsStore';

function renderDetail() {
  return render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/funds/7']}>
        <Routes>
          <Route path="funds/:fundId" element={<FundDetail />} />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('FundDetail (задача 53)', () => {
  beforeEach(() => {
    useFundsStore.setState({ detail: null, detailLoading: false, detailError: null, settingsSaving: false, settingsError: null });
  });

  it('показывает метрики, окна доходности, прогноз с основой и дисклеймер', async () => {
    server.use(http.get('*/api/funds/7', () => HttpResponse.json(baseFundDetail)));
    renderDetail();

    const metrics = await screen.findByTestId('fund-detail-metrics');
    expect(metrics).toHaveTextContent('10');
    expect(metrics).toHaveTextContent('1.35%');
    expect(metrics).toHaveTextContent('13.10%'); // XIRR
    expect(metrics).toHaveTextContent('0.79%'); // TER
    expect(screen.getByTestId('fund-window-12 мес.')).toHaveTextContent('14.25%');
    expect(screen.getByTestId('fund-outlook-basis')).toHaveTextContent('по последним выплатам за 3 мес.');
    expect(screen.getByTestId('fund-outlook-yield')).toHaveTextContent('14.88%');
    expect(screen.getByTestId('fund-outlook-after-tax')).toHaveTextContent('12.95%');
    expect(screen.getByTestId('fund-key-rate-note')).toHaveTextContent('ключевой ставкой');
    expect(screen.getByTestId('fund-operations')).toHaveTextContent('Покупка');
    expect(screen.getByTestId('disclaimer')).toBeInTheDocument();
    expect(screen.getByTestId('fund-income-link')).toHaveAttribute('href', '/income?asset=fund&focus=7');
  });

  it('окно короче 30 дней (annualizedFraction = null) не показывает «в годовых»; нет цены — предупреждение', async () => {
    const detail = {
      ...baseFundDetail,
      fund: {
        ...baseFundDetail.fund,
        dataIncomplete: true,
        performance: { ...baseFundDetail.fund.performance, m1: { returnFraction: 0.01, annualizedFraction: null, days: 28 } },
      },
    };
    server.use(http.get('*/api/funds/7', () => HttpResponse.json(detail)));
    renderDetail();

    const m1 = await screen.findByTestId('fund-window-1 мес.');
    expect(m1).toHaveTextContent('1.00%');
    expect(m1).not.toHaveTextContent('в годовых');
    expect(screen.getByTestId('fund-window-3 мес.')).toHaveTextContent('в годовых');
    expect(screen.getByTestId('fund-detail-incomplete')).toBeInTheDocument();
  });

  it('смена метода прогноза отправляет PUT и рефетчит карточку', async () => {
    let putBody: unknown = null;
    let currentMethod: 'auto' | 'indexYield' = 'auto';
    server.use(
      http.get('*/api/funds/7', () =>
        HttpResponse.json({
          ...baseFundDetail,
          settings: { forecastMethod: currentMethod, benchmarkIndex: null },
          fund: {
            ...baseFund,
            outlook: { ...baseFund.outlook, requestedMethod: currentMethod, method: currentMethod === 'auto' ? 'distributions' : 'indexYield', basisLabel: currentMethod === 'auto' ? 'по последним выплатам за 3 мес.' : 'доходность индекса RGBI 16.03% − комиссия фонда 0.79%' },
          },
        }),
      ),
      http.put('*/api/funds/7/settings', async ({ request }) => {
        putBody = await request.json();
        currentMethod = 'indexYield';
        return HttpResponse.json({ forecastMethod: 'indexYield', benchmarkIndex: null });
      }),
    );
    renderDetail();

    const select = await screen.findByTestId('fund-method-select');
    await userEvent.click(select);
    await userEvent.click(await screen.findByText('По индексу', { selector: 'span' }));

    await waitFor(() => expect(putBody).toEqual({ forecastMethod: 'indexYield', benchmarkIndex: null }));
    await waitFor(() => expect(screen.getByTestId('fund-outlook-basis')).toHaveTextContent('RGBI'));
  });

  it('оценка недоступна — причина вместо чисел', async () => {
    server.use(
      http.get('*/api/funds/7', () =>
        HttpResponse.json({
          ...baseFundDetail,
          fund: { ...baseFund, outlook: { ...baseFund.outlook, expectedAnnualYieldFraction: null, afterTaxYieldFraction: null, expectedIncome1yRub: null, expectedMonthlyIncomeRub: null, unavailableReason: 'нет данных по индексу RGBI' } },
        }),
      ),
    );
    renderDetail();
    expect(await screen.findByTestId('fund-outlook-unavailable')).toHaveTextContent('нет данных по индексу RGBI');
  });

  it('проданный фонд (qty 0) открывается без падения', async () => {
    server.use(
      http.get('*/api/funds/7', () =>
        HttpResponse.json({ ...baseFundDetail, fund: { ...baseFund, quantity: 0, marketValueRub: 0, avgCostRub: 0, investedRub: 0, unrealizedPnlRub: null, unrealizedPnlFraction: null } }),
      ),
    );
    renderDetail();
    expect(await screen.findByTestId('fund-detail-metrics')).toHaveTextContent('фонд продан');
  });

  it('404 / ошибка — ErrorState', async () => {
    renderDetail(); // дефолтный хендлер отдаёт 404
    expect(await screen.findByTestId('fund-detail-error')).toBeInTheDocument();
  });
});
