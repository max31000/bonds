import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { baseSummary } from '../test/fundsFixtures';
import { Dashboard } from './Dashboard';
import { useLiveStore } from '../store/useLiveStore';

function renderDashboard() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    </MantineProvider>,
  );
}

// Деньги на счёте у брокера = 20 000 (валюта 12 000 уже внутри) → рубли 8 000.
const withCurrency = { ...baseSummary, cashRub: 20000, currencyValueRub: 12000, cashRubOnly: 8000, totalRub: 135135, brokerTotalRub: 135135 };

describe('Dashboard — валюта (задача 56)', () => {
  beforeEach(() => {
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
  });

  it('разбивка «Стоимости портфеля»: облигации, фонды, валюта, рубли; итог не задвоен', async () => {
    server.use(http.get('*/api/portfolio/summary', () => HttpResponse.json(withCurrency)));
    renderDashboard();
    const kpi = await screen.findByTestId('kpi-total-balance');
    await waitFor(() => expect(kpi).toHaveTextContent(/135\s135\s₽/));
    expect(kpi).toHaveTextContent(/валюта 12\s000\s₽/);
    expect(kpi).toHaveTextContent(/рубли 8\s000\s₽/);
    // 105 000 + 10 135 + 12 000 + 8 000 = 135 135 — валюта не прибавлена повторно к cashRub.
    expect(withCurrency.bondsValueRub + withCurrency.fundsValueRub + withCurrency.currencyValueRub + withCurrency.cashRubOnly).toBe(withCurrency.totalRub);
  });

  it('«Деньги на счёте»: сумма брокера без изменений и отдельная строка «в т.ч. валюта»', async () => {
    server.use(
      http.get('*/api/portfolio/summary', () => HttpResponse.json(withCurrency)),
      http.get('*/api/live/positions', () => HttpResponse.json({ positions: [], totalMarketValueRub: 0, cashRub: 20000, asOfUtc: new Date().toISOString() })),
    );
    renderDashboard();
    const cash = await screen.findByTestId('kpi-cash');
    await waitFor(() => expect(cash).toHaveTextContent(/20\s000\s₽/));
    expect(await screen.findByTestId('kpi-cash-currency')).toHaveTextContent(/в т\.ч\. валюта 12\s000\s₽/);
  });

  it('без валюты строки «в т.ч. валюта» нет', async () => {
    server.use(http.get('*/api/portfolio/summary', () => HttpResponse.json(baseSummary)));
    renderDashboard();
    await waitFor(() => expect(screen.getByTestId('kpi-total-balance')).toHaveTextContent('123'));
    expect(screen.queryByTestId('kpi-cash-currency')).not.toBeInTheDocument();
    expect(screen.getByTestId('kpi-total-balance')).not.toHaveTextContent('валюта');
  });
});
