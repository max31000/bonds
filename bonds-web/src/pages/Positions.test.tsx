import { render, screen, waitFor, act } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Positions } from './Positions';
import { usePositionsStore } from '../store/usePositionsStore';
import { useAuthStore } from '../store/useAuthStore';
import { useLiveStore } from '../store/useLiveStore';
import type { PositionRow } from '../api/types';

function renderPositions() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Positions />
      </MemoryRouter>
    </MantineProvider>,
  );
}

/**
 * T-21/C.2/D: подменяет window.matchMedia так, чтобы медиа-запрос мобильного брейкпоинта
 * (`(max-width: 48em)`, используется в Positions.tsx через useMediaQuery) возвращал `matches`.
 * Глобальный мок в test/setup.ts всегда возвращает matches: false — этого достаточно для
 * десктопных тестов, но мобильную ветку рендера нужно проверять отдельно.
 */
function mockMobileViewport() {
  const original = window.matchMedia;
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: (query: string) => ({
      matches: query.includes('max-width'),
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }),
  });
  return () => {
    Object.defineProperty(window, 'matchMedia', { writable: true, configurable: true, value: original });
  };
}

const basePosition: PositionRow = {
  positionId: 1,
  instrumentId: 10,
  name: 'ОФЗ 26238',
  isin: 'RU000A1038V6',
  issuer: 'Минфин РФ',
  sector: 'ОФЗ',
  quantity: 100,
  marketValueRub: 105000,
  currencyRub: 'RUB',
  couponType: 'Fixed',
  maturityDate: '2030-01-01',
  horizonDate: '2030-01-01',
  calculatedToOffer: false,
  ytmEffective: 0.125,
  currentYield: 0.118,
  modifiedDuration: 3.2,
  gSpread: 0.005,
  isFloater: false,
  isIndexed: false,
  isEstimated: false,
  dataIncomplete: false,
  isOutOfScopeCurrency: false,
  averageCostRub: 980,
  investedRub: 98000,
  unrealizedPnlRub: 7000,
  unrealizedPnlPercent: 0.0714,
  couponsReceivedRub: 3500,
  totalReturnPercent: 0.1071,
  costBasisIncomplete: false,
};

describe('Positions', () => {
  beforeEach(() => {
    usePositionsStore.setState({ positions: [], disclaimer: '', isLoading: false, error: null });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
  });

  it('renders a regular bond using ytmEffective as the yield', async () => {
    server.use(
      http.get('*/api/positions', () =>
        HttpResponse.json({ positions: [basePosition], disclaimer: 'тестовый дисклеймер' }),
      ),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Минфин РФ')).toBeInTheDocument());
    // T-21/B: строка «Итого» показывает средневзвешенную доходность, которая для единственной
    // позиции портфеля численно совпадает со значением её собственной ячейки — поэтому "12.50%"
    // теперь легитимно встречается дважды (ячейка доходности + строка «Итого»).
    expect(screen.getByTestId('yield-cell-1')).toHaveTextContent('12.50%');
    expect(screen.getByTestId('positions-totals-row')).toHaveTextContent('12.50%');
    expect(screen.queryByText('11.80%')).not.toBeInTheDocument();
    expect(screen.getByText('тестовый дисклеймер')).toBeInTheDocument();
  });

  it('shows currentYield (not ytmEffective) for a floater, with a marker badge', async () => {
    const floater: PositionRow = {
      ...basePosition,
      positionId: 2,
      issuer: 'РЖД',
      couponType: 'Floating',
      isFloater: true,
      ytmEffective: null,
      currentYield: 0.094,
    };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [floater], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('РЖД')).toBeInTheDocument());
    expect(screen.getByText('9.40%')).toBeInTheDocument();
    expect(screen.getByText('плавающая')).toBeInTheDocument();
  });

  it('shows a dataIncomplete badge', async () => {
    const incomplete: PositionRow = {
      ...basePosition,
      positionId: 3,
      issuer: 'Газпром',
      dataIncomplete: true,
    };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [incomplete], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Газпром')).toBeInTheDocument());
    expect(screen.getByText('данные неполные')).toBeInTheDocument();
  });

  it('shows an out-of-scope currency badge for a USD-nominal bond', async () => {
    const currencyBond: PositionRow = {
      ...basePosition,
      positionId: 7,
      issuer: 'НОВАТЭК',
      isOutOfScopeCurrency: true,
      ytmEffective: null,
      gSpread: null,
    };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [currencyBond], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('НОВАТЭК')).toBeInTheDocument());
    expect(screen.getByText('валютная / вне скоупа')).toBeInTheDocument();
  });

  it('shows a calculatedToOffer badge', async () => {
    const toOffer: PositionRow = {
      ...basePosition,
      positionId: 4,
      issuer: 'Сбербанк',
      calculatedToOffer: true,
    };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [toOffer], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Сбербанк')).toBeInTheDocument());
    expect(screen.getByText('к оферте')).toBeInTheDocument();
  });

  it('shows an onboarding prompt to set up the T-Invest token when none is configured', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-empty-no-token')).toBeInTheDocument());
  });

  it('shows a plain empty-state message for an empty portfolio once the token is configured', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [], disclaimer: '' })),
      http.get('*/api/settings', () =>
        HttpResponse.json({
          baseCurrency: 'RUB',
          tInvestTokenConfigured: true,
          tInvestTokenMasked: '...1234',
          upcomingEventDaysThreshold: 14,
          uninvestedCashThresholdRub: 10000,
          uninvestedCashLookbackDays: 7,
          yieldBelowAlternativeBpsThreshold: 50,
          maturityWindowDaysForAlternativeComparison: 30,
          defaultMaxConcentrationPercent: 25,
          durationDriftToleranceYears: 0.5,
        }),
      ),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-empty')).toBeInTheDocument());
  });

  it('sorts by yield when the column header is clicked', async () => {
    const low: PositionRow = { ...basePosition, positionId: 5, issuer: 'Нижняя', ytmEffective: 0.05 };
    const high: PositionRow = { ...basePosition, positionId: 6, issuer: 'Верхняя', ytmEffective: 0.20 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [low, high], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Нижняя')).toBeInTheDocument());

    const rowsBefore = screen.getAllByText(/Верхняя|Нижняя/).map((el) => el.textContent);
    expect(rowsBefore[0]).toBe('Верхняя'); // desc по умолчанию — самая высокая доходность первой

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('sort-yield'));

    await waitFor(() => {
      const rowsAfter = screen.getAllByText(/Верхняя|Нижняя/).map((el) => el.textContent);
      expect(rowsAfter[0]).toBe('Нижняя');
    });
  });

  it('sorts by P&L% when the P&L column header is clicked', async () => {
    const lowPnl: PositionRow = { ...basePosition, positionId: 8, issuer: 'Убыточная', unrealizedPnlPercent: -0.1 };
    const highPnl: PositionRow = { ...basePosition, positionId: 9, issuer: 'Прибыльная', unrealizedPnlPercent: 0.3 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [lowPnl, highPnl], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Убыточная')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('sort-pnl'));

    await waitFor(() => {
      const rows = screen.getAllByText(/Прибыльная|Убыточная/).map((el) => el.textContent);
      expect(rows[0]).toBe('Прибыльная'); // desc после первого клика — самый высокий P&L% первым
    });

    await userEvent.click(screen.getByTestId('sort-pnl'));

    await waitFor(() => {
      const rows = screen.getAllByText(/Прибыльная|Убыточная/).map((el) => el.textContent);
      expect(rows[0]).toBe('Убыточная');
    });
  });

  it('renders cost-basis columns (average cost, P&L rub/percent, coupons received)', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Минфин РФ')).toBeInTheDocument());
    expect(screen.getByText('980 ₽')).toBeInTheDocument(); // averageCostRub
    expect(screen.getByText('7 000 ₽')).toBeInTheDocument(); // unrealizedPnlRub
    expect(screen.getByText('7.14%')).toBeInTheDocument(); // unrealizedPnlPercent
    expect(screen.getByText('3 500 ₽')).toBeInTheDocument(); // couponsReceivedRub
  });

  it('shows P&L in green when positive and red when negative', async () => {
    const losing: PositionRow = { ...basePosition, positionId: 11, issuer: 'Просевшая', unrealizedPnlRub: -5000, unrealizedPnlPercent: -0.05 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition, losing], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Просевшая')).toBeInTheDocument());

    const gainText = screen.getByText('7 000 ₽');
    const lossText = screen.getByText('-5 000 ₽');
    expect(gainText.getAttribute('style')).toContain('--mantine-color-green-text');
    expect(lossText.getAttribute('style')).toContain('--mantine-color-red-text');
  });

  it('shows a grey "журнал неполон" badge when costBasisIncomplete is true', async () => {
    const incompleteJournal: PositionRow = {
      ...basePosition,
      positionId: 12,
      issuer: 'Старая позиция',
      costBasisIncomplete: true,
      averageCostRub: null,
      unrealizedPnlRub: null,
      unrealizedPnlPercent: null,
      totalReturnPercent: null,
    };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [incompleteJournal], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Старая позиция')).toBeInTheDocument());
    expect(screen.getByText('журнал неполон')).toBeInTheDocument();
  });

  it('shows a yield explanation tooltip icon next to the "Доходность" header', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('yield-info-icon')).toBeInTheDocument());
  });

  it('shows an error state without crashing when the request fails (non-401)', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-error')).toBeInTheDocument());
  });

  it('logs out and would redirect to /login on a 401 response', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ error: 'unauthorized' }, { status: 401 })),
    );

    const originalLocation = window.location;
    Object.defineProperty(window, 'location', {
      value: { ...originalLocation, href: '' },
      writable: true,
    });

    renderPositions();

    await waitFor(() => expect(useAuthStore.getState().token).toBeNull());
    expect(window.location.href).toContain('/login');

    Object.defineProperty(window, 'location', { value: originalLocation, writable: true });
  });

  // ─── T-16/B: live-merge рыночной стоимости поверх статичных данных GET /api/positions ────

  it('shows the static market value when no live price has arrived yet', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('live-market-value-1')).toBeInTheDocument());
    expect(screen.getByTestId('live-market-value-1')).toHaveTextContent('105 000');
  });

  it('overrides the market value with the live price and shows the day change once useLiveStore has data', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
    );

    renderPositions();
    await waitFor(() => expect(screen.getByTestId('live-market-value-1')).toBeInTheDocument());

    act(() => {
      useLiveStore.getState().setLivePositions(
        [
          {
            positionId: 1,
            instrumentId: 10,
            lastPriceRub: 1060,
            marketValueRub: 106000,
            changeDayPercent: 0.0095,
            isStale: false,
            asOfUtc: '2026-07-03T10:00:00Z',
          },
        ],
        106000,
        '2026-07-03T10:00:00Z',
      );
    });

    await waitFor(() => expect(screen.getByTestId('live-market-value-1')).toHaveTextContent('106 000'));
    expect(screen.getByText('+0.95%')).toBeInTheDocument();
  });

  // ─── T-19: клик по строке ведёт на карточку позиции ───────────────────────────────────────

  it('navigates to the position detail page when a row is clicked', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
    );

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/positions']}>
          <Routes>
            <Route path="/positions" element={<Positions />} />
            <Route path="/positions/:id" element={<div data-testid="position-detail-stub">detail page</div>} />
          </Routes>
        </MemoryRouter>
      </MantineProvider>,
    );

    await waitFor(() => expect(screen.getByTestId('position-row-1')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('position-row-1'));

    await waitFor(() => expect(screen.getByTestId('position-detail-stub')).toBeInTheDocument());
  });

  it('marks a stale live fallback with the last full-sync time', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
    );

    renderPositions();
    await waitFor(() => expect(screen.getByTestId('live-market-value-1')).toBeInTheDocument());

    act(() => {
      useLiveStore.getState().setLivePositions(
        [
          {
            positionId: 1,
            instrumentId: 10,
            lastPriceRub: 1050,
            marketValueRub: 105000,
            changeDayPercent: null,
            isStale: true,
            asOfUtc: '2026-07-03T09:00:00Z',
          },
        ],
        105000,
        '2026-07-03T09:00:00Z',
      );
    });

    await waitFor(() => expect(screen.getByTestId('live-stale-1')).toBeInTheDocument());
  });

  // ─── T-21/B.3: строка «Итого» ──────────────────────────────────────────────────────────────

  it('renders a totals row with the summed market value and weighted-average yield/duration', async () => {
    const positionA: PositionRow = { ...basePosition, positionId: 21, marketValueRub: 100_000, ytmEffective: 0.1, modifiedDuration: 2 };
    const positionB: PositionRow = { ...basePosition, positionId: 22, marketValueRub: 300_000, ytmEffective: 0.2, modifiedDuration: 4 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [positionA, positionB], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-totals-row')).toBeInTheDocument());
    const totalsRow = screen.getByTestId('positions-totals-row');
    expect(totalsRow).toHaveTextContent('400 000'); // сумма рыночной стоимости
    expect(totalsRow).toHaveTextContent('17.50%'); // (100k*0.10 + 300k*0.20)/400k
    expect(totalsRow).toHaveTextContent('3.50'); // (100k*2 + 300k*4)/400k
  });

  it('shows a floater-exclusion footnote in the totals row when a floater is present', async () => {
    const floater: PositionRow = { ...basePosition, positionId: 23, isFloater: true, ytmEffective: null, currentYield: 0.3 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition, floater], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-totals-row')).toBeInTheDocument());
    expect(screen.getByText(/без флоатеров/)).toBeInTheDocument();
  });

  // ─── T-21/B.1: heatmap колонки «Доходность» ────────────────────────────────────────────────

  it('gives the highest-yield regular bond a more saturated heatmap background than the lowest', async () => {
    const low: PositionRow = { ...basePosition, positionId: 31, issuer: 'Низкая', ytmEffective: 0.05 };
    const high: PositionRow = { ...basePosition, positionId: 32, issuer: 'Высокая', ytmEffective: 0.25 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [low, high], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('yield-cell-31')).toBeInTheDocument());
    const lowStyle = screen.getByTestId('yield-cell-31').getAttribute('style') ?? '';
    const highStyle = screen.getByTestId('yield-cell-32').getAttribute('style') ?? '';
    const extractPercent = (style: string) => Number(/(\d+)%/.exec(style)?.[1] ?? NaN);
    expect(extractPercent(highStyle)).toBeGreaterThan(extractPercent(lowStyle));
  });

  it('does not apply a heatmap background to a floater yield cell', async () => {
    const floater: PositionRow = { ...basePosition, positionId: 33, isFloater: true, ytmEffective: null, currentYield: 0.09 };
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [floater], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('yield-cell-33')).toBeInTheDocument());
    const style = screen.getByTestId('yield-cell-33').getAttribute('style');
    expect(style ?? '').not.toContain('color-mix');
  });

  // ─── T-21/C.2: карточки вместо таблицы на мобиле ───────────────────────────────────────────

  describe('mobile layout (< 768px)', () => {
    it('renders position cards instead of the table when the viewport is narrow', async () => {
      const restore = mockMobileViewport();
      server.use(
        http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
      );

      renderPositions();

      await waitFor(() => expect(screen.getByTestId('positions-cards')).toBeInTheDocument());
      expect(screen.queryByTestId('positions-table')).not.toBeInTheDocument();
      expect(screen.getByTestId(`position-card-${basePosition.positionId}`)).toBeInTheDocument();

      restore();
    });

    it('expands additional metrics in a card when "Подробнее" is clicked', async () => {
      const restore = mockMobileViewport();
      server.use(
        http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
      );

      renderPositions();

      await waitFor(() => expect(screen.getByTestId(`position-card-${basePosition.positionId}`)).toBeInTheDocument());

      const { default: userEvent } = await import('@testing-library/user-event');
      await userEvent.click(screen.getByTestId(`position-card-toggle-${basePosition.positionId}`));

      await waitFor(() => expect(screen.getByTestId(`position-card-details-${basePosition.positionId}`)).toBeVisible());

      restore();
    });

    it('navigates to the position detail page when a card is tapped', async () => {
      const restore = mockMobileViewport();
      server.use(
        http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
      );

      render(
        <MantineProvider>
          <MemoryRouter initialEntries={['/positions']}>
            <Routes>
              <Route path="/positions" element={<Positions />} />
              <Route path="/positions/:id" element={<div data-testid="position-detail-stub">detail page</div>} />
            </Routes>
          </MemoryRouter>
        </MantineProvider>,
      );

      await waitFor(() => expect(screen.getByTestId(`position-card-open-${basePosition.positionId}`)).toBeInTheDocument());

      const { default: userEvent } = await import('@testing-library/user-event');
      await userEvent.click(screen.getByTestId(`position-card-open-${basePosition.positionId}`));

      await waitFor(() => expect(screen.getByTestId('position-detail-stub')).toBeInTheDocument());

      restore();
    });

    it('shows a sort Select above the card list on mobile', async () => {
      const restore = mockMobileViewport();
      server.use(
        http.get('*/api/positions', () => HttpResponse.json({ positions: [basePosition], disclaimer: '' })),
      );

      renderPositions();

      await waitFor(() => expect(screen.getByTestId('mobile-sort-select')).toBeInTheDocument());

      restore();
    });
  });
});
