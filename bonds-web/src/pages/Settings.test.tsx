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
  commissionRateOverride: null,
  commissionAutoEstimate: null,
  tInvestTariff: null,
  commissionEffectiveRate: 0.003,
  commissionEffectiveSource: 'Default',
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

  // T-13/C: PUT /api/settings/tinvest-token валидирует токен перед сохранением — 422 не должен
  // отображаться как "задан", и текст ошибки от бэкенда должен дойти до пользователя.
  it('shows a validation error notification and does not mark the token as configured on 422', async () => {
    server.use(
      http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
      http.put('*/api/settings/tinvest-token', () =>
        HttpResponse.json(
          { error: 'Токен не принят T-Invest (недействителен или отозван).', type: 'TInvestTokenValidationException' },
          { status: 422 },
        ),
      ),
    );

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('tinvest-token-input')).toBeInTheDocument());

    const { default: userEvent } = await import('@testing-library/user-event');
    const input = screen.getByTestId('tinvest-token-input') as HTMLInputElement;
    await userEvent.type(input, 'invalid-token-value');
    await userEvent.click(screen.getByTestId('tinvest-token-save'));

    await waitFor(() => expect(screen.getByText('Токен не прошёл проверку')).toBeInTheDocument());
    expect(screen.getAllByText('Токен не принят T-Invest (недействителен или отозван).').length).toBeGreaterThan(0);
    expect(input.value).toBe('');
    expect(screen.getByText('не задан')).toBeInTheDocument();
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

  // Plan/22 часть D: блок «Комиссия брокера» — тариф T-Invest, авто-оценка из журнала, применённая ставка+источник.
  describe('commission section (plan/22)', () => {
    it('shows tariff, auto-estimate, and effective rate from the journal', async () => {
      server.use(
        http.get('*/api/settings', () =>
          HttpResponse.json({
            ...baseSettings,
            tInvestTariff: 'Инвестор',
            commissionAutoEstimate: {
              rate: 0.00046,
              turnoverRub: 150000,
              feeTotalRub: 69,
              tradeCount: 23,
              windowMonths: 6,
            },
            commissionEffectiveRate: 0.00046,
            commissionEffectiveSource: 'EstimatedFromTrades',
          }),
        ),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('commission-section')).toBeInTheDocument());
      expect(screen.getByTestId('commission-tariff')).toHaveTextContent('Инвестор');
      expect(screen.getByTestId('commission-auto-estimate')).toHaveTextContent('23 сделок');
      expect(screen.getByTestId('commission-effective')).toHaveTextContent('из ваших сделок');
    });

    it('shows a placeholder when the journal has no estimate and effective rate falls back to default', async () => {
      server.use(http.get('*/api/settings', () => HttpResponse.json(baseSettings)));

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('commission-section')).toBeInTheDocument());
      expect(screen.getByTestId('commission-tariff')).toHaveTextContent('—');
      expect(screen.getByTestId('commission-auto-estimate')).toHaveTextContent('недостаточно данных');
      expect(screen.getByTestId('commission-effective')).toHaveTextContent('дефолт 0.3%');
    });

    it('converts the percent input to a fraction when saving an override', async () => {
      server.use(
        http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
        http.put('*/api/settings', async ({ request }) => {
          const body = (await request.json()) as Record<string, unknown>;
          expect(body.commissionRateOverride).toBeCloseTo(0.0005);
          return HttpResponse.json({
            ...baseSettings,
            commissionRateOverride: 0.0005,
            commissionEffectiveRate: 0.0005,
            commissionEffectiveSource: 'UserOverride',
          });
        }),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('commission-override-input')).toBeInTheDocument());

      const { default: userEvent } = await import('@testing-library/user-event');
      const input = screen.getByTestId('commission-override-input') as HTMLInputElement;
      await userEvent.clear(input);
      await userEvent.type(input, '0.05');
      await userEvent.click(screen.getByTestId('commission-save'));

      await waitFor(() => expect(screen.getByText('Комиссия сохранена')).toBeInTheDocument());
    });

    it('shows an error notification when the override is out of range (422)', async () => {
      server.use(
        http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
        http.put('*/api/settings', () =>
          HttpResponse.json(
            { error: 'Ставка комиссии должна быть в диапазоне (0, 0.05)', type: 'ValidationException' },
            { status: 422 },
          ),
        ),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('commission-override-input')).toBeInTheDocument());

      const { default: userEvent } = await import('@testing-library/user-event');
      const input = screen.getByTestId('commission-override-input') as HTMLInputElement;
      await userEvent.clear(input);
      await userEvent.type(input, '10');
      await userEvent.click(screen.getByTestId('commission-save'));

      await waitFor(() => expect(screen.getByText('Не удалось сохранить комиссию')).toBeInTheDocument());
    });
  });
});
