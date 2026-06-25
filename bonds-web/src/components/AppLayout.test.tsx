import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { AppLayout } from './AppLayout';
import { useAuthStore } from '../store/useAuthStore';
import { useSignalsStore } from '../store/useSignalsStore';
import { useSyncStore } from '../store/useSyncStore';

function renderLayout() {
  return render(
    <MantineProvider>
      <Notifications />
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<div>Контент</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('AppLayout', () => {
  beforeEach(() => {
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
    useSignalsStore.setState({ signals: [], isLoading: false, error: null });
    useSyncStore.setState({ isRunning: false, lastResult: null, error: null });
  });

  it('shows an unread signal count badge in the nav', async () => {
    server.use(
      http.get('*/api/signals', () =>
        HttpResponse.json({
          signals: [
            { id: 1, type: 'Coupon', severity: 'Low', positionId: 1, instrumentId: 1, suggestedAction: '', date: '2026-06-20', isRead: false },
          ],
        }),
      ),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByText('1')).toBeInTheDocument());
  });

  it('triggers a sync and shows a success notification with a summary', async () => {
    server.use(
      http.post('*/api/sync', () =>
        HttpResponse.json({
          alreadyRunning: false,
          noAccountConfigured: false,
          instrumentsSynced: 12,
          operationsUpserted: 3,
          yieldCurveUpdated: true,
          positionsProjected: 5,
          flowsWritten: 5,
          snapshotStored: true,
          signalsCreated: 2,
          errors: [],
          hasErrors: false,
        }),
      ),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('force-sync-button')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('force-sync-button'));

    await waitFor(() => expect(screen.getByText('Данные обновлены')).toBeInTheDocument());
  });

  it('shows an error notification when sync completes with errors', async () => {
    server.use(
      http.post('*/api/sync', () =>
        HttpResponse.json({
          alreadyRunning: false,
          noAccountConfigured: false,
          instrumentsSynced: 0,
          operationsUpserted: 0,
          yieldCurveUpdated: false,
          positionsProjected: 0,
          flowsWritten: 0,
          snapshotStored: false,
          signalsCreated: 0,
          errors: ['MOEX недоступен'],
          hasErrors: true,
        }),
      ),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('force-sync-button')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('force-sync-button'));

    await waitFor(() => expect(screen.getByText('Обновление завершилось с ошибками')).toBeInTheDocument());
  });
});
