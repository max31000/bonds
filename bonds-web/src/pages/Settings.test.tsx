import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Settings } from './Settings';
import { useSettingsStore } from '../store/useSettingsStore';
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
  defaultMaxConcentrationPercent: 25,
  commissionRateOverride: null,
  replacementHorizonYears: 2,
  spreadWideningThresholdFraction: 0.005,
  priceDrawdownThresholdFraction: 0.05,
  commissionAutoEstimate: null,
  tInvestTariff: null,
  commissionEffectiveRate: 0.003,
  commissionEffectiveSource: 'Default',
};

describe('Settings', () => {
  beforeEach(() => {
    useSettingsStore.setState({ settings: null, isLoading: false, error: null });
  });

  it('renders the thresholds form and the connections section', async () => {
    server.use(http.get('*/api/settings', () => HttpResponse.json(baseSettings)));

    renderSettings();

    await waitFor(() => expect(screen.getByTestId('thresholds-section')).toBeInTheDocument());
    expect(screen.getByTestId('credentials-section')).toBeInTheDocument();
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

  // Задача 48: горизонт анализа замены — настройка (годы), заменяет фронтовую константу.
  describe('replacement horizon section', () => {
    it('shows the current horizon from settings', async () => {
      server.use(http.get('*/api/settings', () => HttpResponse.json({ ...baseSettings, replacementHorizonYears: 3.5 })));

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('horizon-section')).toBeInTheDocument());
      await waitFor(() => expect(screen.getByTestId('replacement-horizon-input')).toHaveValue('3.5'));
    });

    it('saves the new horizon (years) together with the unchanged settings', async () => {
      let putBody: Record<string, unknown> | null = null;
      server.use(
        http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
        http.put('*/api/settings', async ({ request }) => {
          putBody = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({ ...baseSettings, ...putBody });
        }),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('replacement-horizon-input')).toBeInTheDocument());
      const { default: userEvent } = await import('@testing-library/user-event');
      const input = screen.getByTestId('replacement-horizon-input');
      await userEvent.clear(input);
      await userEvent.type(input, '4.5');
      await userEvent.click(screen.getByTestId('replacement-horizon-save'));

      await waitFor(() => expect(screen.getByText('Горизонт сохранён')).toBeInTheDocument());
      expect(putBody).toMatchObject({
        replacementHorizonYears: 4.5,
        upcomingEventDaysThreshold: 14,
        commissionRateOverride: null,
      });
    });

    it('keeps the horizon when saving the commission override (all settings go in one PUT body)', async () => {
      let putBody: Record<string, unknown> | null = null;
      server.use(
        http.get('*/api/settings', () => HttpResponse.json({ ...baseSettings, replacementHorizonYears: 6 })),
        http.put('*/api/settings', async ({ request }) => {
          putBody = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({ ...baseSettings, ...putBody });
        }),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('commission-save')).toBeInTheDocument());
      const { default: userEvent } = await import('@testing-library/user-event');
      await userEvent.click(screen.getByTestId('commission-save'));

      await waitFor(() => expect(putBody).not.toBeNull());
      expect(putBody).toMatchObject({ replacementHorizonYears: 6 });
    });
  });

  // Задача 49: пороги рыночных событий — UI вводит б.п. и %, бэкенд хранит ДОЛИ.
  describe('market events thresholds (T-49)', () => {
    it('shows the thresholds converted from fractions: 0.008 → 80 б.п., 0.07 → 7%', async () => {
      server.use(
        http.get('*/api/settings', () =>
          HttpResponse.json({ ...baseSettings, spreadWideningThresholdFraction: 0.008, priceDrawdownThresholdFraction: 0.07 }),
        ),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('market-events-section')).toBeInTheDocument());
      await waitFor(() => expect(screen.getByTestId('spread-widening-input')).toHaveValue('80'));
      expect(screen.getByTestId('price-drawdown-input')).toHaveValue('7');
    });

    it('sends the thresholds as fractions (б.п. ÷ 10000, % ÷ 100) in one PUT', async () => {
      let putBody: Record<string, unknown> | null = null;
      server.use(
        http.get('*/api/settings', () => HttpResponse.json(baseSettings)),
        http.put('*/api/settings', async ({ request }) => {
          putBody = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({ ...baseSettings, ...putBody });
        }),
      );

      renderSettings();

      await waitFor(() => expect(screen.getByTestId('spread-widening-input')).toHaveValue('50'));
      const { default: userEvent } = await import('@testing-library/user-event');
      const spread = screen.getByTestId('spread-widening-input');
      await userEvent.clear(spread);
      await userEvent.type(spread, '80');
      const drawdown = screen.getByTestId('price-drawdown-input');
      await userEvent.clear(drawdown);
      await userEvent.type(drawdown, '8');
      await userEvent.click(screen.getByTestId('market-events-save'));

      await waitFor(() => expect(screen.getByText('Пороги сохранены')).toBeInTheDocument());
      expect(putBody).toMatchObject({
        spreadWideningThresholdFraction: 0.008,
        priceDrawdownThresholdFraction: 0.08,
        upcomingEventDaysThreshold: 14,
        replacementHorizonYears: 2,
      });
    });
  });

  // Блок «Комиссия брокера» — тариф T-Invest, авто-оценка из журнала, применённая ставка+источник.
  describe('commission section', () => {
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

  it('shows the "Данные эмитента" block even without an INN and opens the forms after a valid INN is entered (task 47)', async () => {
    server.use(http.get('*/api/settings', () => HttpResponse.json(baseSettings)));

    renderSettings();

    const section = await screen.findByTestId('issuer-data-section');
    expect(section).toBeInTheDocument();
    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.type(screen.getByTestId('issuer-inn-input'), '7707083893');
    await userEvent.click(screen.getByTestId('issuer-inn-apply'));

    expect(await screen.findByTestId('issuer-ratings-toggle')).toBeInTheDocument();
    expect(screen.getByTestId('issuer-financials-toggle')).toBeInTheDocument();
  });
});
