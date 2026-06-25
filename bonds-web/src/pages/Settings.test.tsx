import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Settings } from './Settings';
import { useSettingsStore } from '../store/useSettingsStore';
import { useAuthStore } from '../store/useAuthStore';
import type { SettingsResponse } from '../api/types';

function renderSettings() {
  return render(
    <MantineProvider>
      <Notifications />
      <MemoryRouter>
        <Settings />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const baseSettings: SettingsResponse = {
  baseCurrency: 'RUB',
  tInvestTokenConfigured: false,
  tInvestTokenMasked: null,
  upcomingEventDaysThreshold: 14,
  uninvestedCashThresholdRub: 10000,
  uninvestedCashLookbackDays: 7,
  yieldBelowAlternativeBpsThreshold: 50,
  maturityWindowDaysForAlternativeComparison: 30,
  defaultMaxConcentrationPercent: 25,
  durationDriftToleranceYears: 0.5,
};

describe('Settings', () => {
  beforeEach(() => {
    useSettingsStore.setState({ settings: null, isLoading: false, error: null });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });
  });

  it('renders the thresholds form and token section', async () => {
    server.use(http.get('*/api/settings', () => HttpResponse.json(baseSettings)));

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('thresholds-section')).toBeInTheDocument());
    expect(screen.getByTestId('tinvest-token-section')).toBeInTheDocument();
    expect(screen.getByText('не задан')).toBeInTheDocument();
  });

  it('shows the masked token and "configured" status when a token is already set', async () => {
    server.use(
      http.get('*/api/settings', () =>
        HttpResponse.json({ ...baseSettings, tInvestTokenConfigured: true, tInvestTokenMasked: '...1234' }),
      ),
    );

    renderSettings();

    await waitFor(() => expect(screen.getByText(/задан: \.\.\.1234/)).toBeInTheDocument());
  });

  it('clears the token input after saving and never echoes a token value back', async () => {
    server.use(
      http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
      http.put('*/api/settings/tinvest-token', () =>
        HttpResponse.json({ tInvestTokenConfigured: true, tInvestTokenMasked: '...5678' }),
      ),
    );

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('tinvest-token-input')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    const input = screen.getByTestId('tinvest-token-input') as HTMLInputElement;
    await userEvent.type(input, 'secret-token-value');
    await userEvent.click(screen.getByTestId('tinvest-token-save'));

    await waitFor(() => expect(screen.getByText(/задан: \.\.\.5678/)).toBeInTheDocument());
    expect(input.value).toBe('');
    expect(screen.queryByText('secret-token-value')).not.toBeInTheDocument();
  });

  it('saves the thresholds form', async () => {
    server.use(
      http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
      http.put('*/api/settings', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({
          ...baseSettings,
          ...body,
        });
      }),
    );

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('thresholds-save')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByTestId('thresholds-save'));

    await waitFor(() => expect(screen.getByText('Настройки сохранены')).toBeInTheDocument());
  });

  it('shows an error state without crashing when the request fails', async () => {
    server.use(http.get('*/api/settings', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('settings-error')).toBeInTheDocument());
  });
});
