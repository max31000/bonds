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
  issuer: 'Минфин РФ',
  sector: 'ОФЗ',
  quantity: 100,
  marketValueRub: 105000,
  currencyRub: 'RUB',
  couponType: 'Fixed',
  maturityDate: '2030-01-01',
  horizonDate: '2030-01-01',
  calculatedToOffer: false,
  ytmEffective: 12.5,
  currentYield: 11.8,
  modifiedDuration: 3.2,
  gSpread: 50,
  isFloater: false,
  isIndexed: false,
  isEstimated: false,
  dataIncomplete: false,
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
      currentYield: 9.4,
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

  it('shows an empty-state message for an empty portfolio', async () => {
    server.use(
      http.get('*/api/positions', () => HttpResponse.json({ positions: [], disclaimer: '' })),
    );

    renderPositions();

    await waitFor(() => expect(screen.getByTestId('positions-empty')).toBeInTheDocument());
  });

  it('sorts by yield when the column header is clicked', async () => {
    const low: PositionRow = { ...basePosition, positionId: 5, issuer: 'Нижняя', ytmEffective: 5 };
    const high: PositionRow = { ...basePosition, positionId: 6, issuer: 'Верхняя', ytmEffective: 20 };
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
