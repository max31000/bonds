import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Positions } from './Positions';
import { usePositionsStore } from '../store/usePositionsStore';
import { useAuthStore } from '../store/useAuthStore';
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
  });

  it('renders a regular bond using ytmEffective as the yield', async () => {
    server.use(
      http.get('*/api/positions', () =>
        HttpResponse.json({ positions: [basePosition], disclaimer: 'тестовый дисклеймер' }),
      ),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByText('Минфин РФ')).toBeInTheDocument());
    expect(screen.getByText('12.50%')).toBeInTheDocument();
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
});
