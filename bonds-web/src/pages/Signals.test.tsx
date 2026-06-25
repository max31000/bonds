import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Signals } from './Signals';
import { useSignalsStore } from '../store/useSignalsStore';
import { useAuthStore } from '../store/useAuthStore';
import type { Signal } from '../api/types';

function renderSignals() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Signals />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const highSignal: Signal = {
  id: 1,
  type: 'OfferPut',
  severity: 'High',
  positionId: 5,
  instrumentId: 10,
  suggestedAction: 'Решите, предъявлять ли бумагу к оферте до 2026-07-01.',
  date: '2026-06-20',
  isRead: false,
};

const lowSignal: Signal = {
  id: 2,
  type: 'Coupon',
  severity: 'Low',
  positionId: 6,
  instrumentId: 11,
  suggestedAction: 'Ожидается купонная выплата.',
  date: '2026-06-15',
  isRead: true,
};

describe('Signals', () => {
  beforeEach(() => {
    useSignalsStore.setState({ signals: [], isLoading: false, error: null });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
  });

  it('renders signals sorted with high severity first and shows a badge for unread', async () => {
    server.use(
      http.get('*/api/signals', () => HttpResponse.json({ signals: [lowSignal, highSignal] })),
    );

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signal-1')).toBeInTheDocument());
    const items = screen.getAllByText(/Решите, предъявлять|Ожидается купонная/);
    expect(items[0].textContent).toMatch(/оферте/);
    expect(screen.getByText('новое')).toBeInTheDocument();
  });

  it('marks a signal read and removes the unread badge/button', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ signals: [highSignal] })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('mark-read-1')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('mark-read-1'));

    await waitFor(() => expect(screen.queryByTestId('mark-read-1')).not.toBeInTheDocument());
  });

  it('filters to unread only', async () => {
    server.use(
      http.get('*/api/signals', () => HttpResponse.json({ signals: [lowSignal, highSignal] })),
    );

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signal-1')).toBeInTheDocument());
    expect(screen.getByTestId('signal-2')).toBeInTheDocument();

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByText('Непрочитанные'));

    await waitFor(() => expect(screen.queryByTestId('signal-2')).not.toBeInTheDocument());
    expect(screen.getByTestId('signal-1')).toBeInTheDocument();
  });

  it('shows an empty state when there are no signals', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ signals: [] })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signals-empty')).toBeInTheDocument());
  });

  it('shows an error state without crashing when the request fails', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signals-error')).toBeInTheDocument());
  });
});
