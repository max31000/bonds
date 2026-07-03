import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
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
            <Route path="/settings" element={<div>Экран настроек</div>} />
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
    // Очередь уведомлений Mantine — module-level singleton, переживает размонтирование
    // <Notifications> между тестами; без явной очистки уведомления предыдущего теста
    // остаются в DOM следующего рендера и ломают getByText (несколько совпадений).
    notifications.clean();
    // Plan/16 часть B: автосинк-при-открытии сравнивает lastSuccessAtUtc с "сейчас" по порогу 12ч —
    // фиксируем системное время, чтобы тесты не зависели от реальной даты запуска.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-07-03T12:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
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
          tokenMissingOrInvalid: false,
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

  // ─── T-13/B: индикатор здоровья синка в шапке (три состояния) ─────────────────────────────

  it('shows a green dot with relative time when the last sync succeeded', async () => {
    server.use(
      http.get('*/api/sync/status', () =>
        HttpResponse.json({
          isRunning: false,
          lastRunStartedAtUtc: '2026-07-03T10:00:00Z',
          lastSuccessAtUtc: '2026-07-03T10:00:00Z',
          lastFailureAtUtc: null,
          lastRunErrors: [],
          tokenMissingOrInvalid: false,
        }),
      ),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('sync-health-ok')).toBeInTheDocument());
    expect(screen.getByText(/синк/)).toBeInTheDocument();
  });

  it('shows a red "sync error" badge with the first error in a tooltip when the last run failed after the last success', async () => {
    server.use(
      http.get('*/api/sync/status', () =>
        HttpResponse.json({
          isRunning: false,
          lastRunStartedAtUtc: '2026-07-03T10:05:00Z',
          lastSuccessAtUtc: '2026-07-03T09:00:00Z',
          lastFailureAtUtc: '2026-07-03T10:05:00Z',
          lastRunErrors: ['MOEX недоступен'],
          tokenMissingOrInvalid: false,
        }),
      ),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('sync-health-error-badge')).toBeInTheDocument());
    expect(screen.getByText('Ошибка синка')).toBeInTheDocument();
  });

  it('shows an orange "token missing/invalid" badge linking to settings when the token is not configured or undecryptable', async () => {
    server.use(
      http.get('*/api/sync/status', () =>
        HttpResponse.json({
          isRunning: false,
          lastRunStartedAtUtc: '2026-07-03T10:05:00Z',
          lastSuccessAtUtc: null,
          lastFailureAtUtc: '2026-07-03T10:05:00Z',
          lastRunErrors: ['T-Invest token is not configured.'],
          tokenMissingOrInvalid: true,
        }),
      ),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('sync-health-token-badge')).toBeInTheDocument());
    expect(screen.getByText('Токен не подключён / недействителен')).toBeInTheDocument();

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('sync-health-token-badge'));

    await waitFor(() => expect(screen.getByText('Экран настроек')).toBeInTheDocument());
  });

  // ─── T-16/B: тихий фоновый синк при открытии после долгого перерыва ────────────────────────

  it('triggers a background sync on mount when the last success is older than 12 hours', async () => {
    let syncCalled = false;
    server.use(
      http.get('*/api/sync/status', () =>
        HttpResponse.json({
          isRunning: false,
          lastRunStartedAtUtc: '2026-07-02T10:00:00Z',
          lastSuccessAtUtc: '2026-07-02T10:00:00Z', // > 12ч до "сейчас" (2026-07-03)
          lastFailureAtUtc: null,
          lastRunErrors: [],
          tokenMissingOrInvalid: false,
        }),
      ),
      http.post('*/api/sync', () => {
        syncCalled = true;
        return HttpResponse.json({
          alreadyRunning: false,
          noAccountConfigured: false,
          tokenMissingOrInvalid: false,
          instrumentsSynced: 5,
          operationsUpserted: 1,
          yieldCurveUpdated: true,
          positionsProjected: 5,
          flowsWritten: 5,
          snapshotStored: true,
          signalsCreated: 0,
          errors: [],
          hasErrors: false,
        });
      }),
    );

    renderLayout();

    await waitFor(() => expect(syncCalled).toBe(true));
    await waitFor(() => expect(screen.getByText('Фоновое обновление данных')).toBeInTheDocument());
  });

  it('does not trigger a background sync when the last success is recent', async () => {
    let syncCalled = false;
    server.use(
      http.get('*/api/sync/status', () =>
        HttpResponse.json({
          isRunning: false,
          lastRunStartedAtUtc: '2026-07-03T09:00:00Z',
          lastSuccessAtUtc: '2026-07-03T09:00:00Z', // менее 12ч назад
          lastFailureAtUtc: null,
          lastRunErrors: [],
          tokenMissingOrInvalid: false,
        }),
      ),
      http.post('*/api/sync', () => {
        syncCalled = true;
        return HttpResponse.json({});
      }),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('sync-health-ok')).toBeInTheDocument());
    expect(syncCalled).toBe(false);
  });

  it('does not trigger a background sync when a sync is already running', async () => {
    let syncCalled = false;
    server.use(
      http.get('*/api/sync/status', () =>
        HttpResponse.json({
          isRunning: true,
          lastRunStartedAtUtc: '2026-07-03T10:00:00Z',
          lastSuccessAtUtc: '2026-07-01T10:00:00Z', // старое, но синк уже бежит
          lastFailureAtUtc: null,
          lastRunErrors: [],
          tokenMissingOrInvalid: false,
        }),
      ),
      http.post('*/api/sync', () => {
        syncCalled = true;
        return HttpResponse.json({});
      }),
    );

    renderLayout();

    await waitFor(() => expect(screen.getByTestId('force-sync-button')).toBeInTheDocument());
    expect(syncCalled).toBe(false);
  });
});
