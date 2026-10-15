import { render, screen, within, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, afterEach, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { COMPONENTS, greenFairSignals, redCheapSignals, reliabilityResponse } from '../test/scorecardFixtures';
import { ReliabilityScoreCard } from './ReliabilityScoreCard';
import { ReliabilityDot, RiskSignalBadges, RELIABILITY_DISCLAIMER } from './RiskSignalBadges';
import type { RiskSignals } from '../api/types';

function renderWithMantine(ui: React.ReactElement) {
  return render(<MantineProvider>{ui}</MantineProvider>);
}

describe('ReliabilityScoreCard', () => {
  it('shows light, score, coverage, critical flag badges with human labels and the disclaimer', () => {
    renderWithMantine(<ReliabilityScoreCard riskSignals={redCheapSignals} testIdSuffix="t" />);

    expect(screen.getByTestId('score-card-light-t')).toHaveAttribute('data-reliability', 'Red');
    expect(screen.getByTestId('score-card-score-t')).toHaveTextContent('42'); // 41.6 → 42
    // coverage — ДОЛЯ 0.78, на экране «78%» (умножение один раз)
    expect(screen.getByTestId('score-card-coverage-t')).toHaveTextContent('данных: 78%');
    expect(within(screen.getByTestId('score-flag-critical-InterestUncovered')).queryByText(/InterestUncovered/)).toBeNull();
    expect(screen.getByTestId('score-flag-critical-InterestUncovered')).toHaveTextContent('Прибыль не покрывает проценты');
    expect(screen.getByTestId('score-flag-warning-PriceDrawdown')).toHaveTextContent('Просадка цены');
    expect(screen.getByTestId('score-card-disclaimer-t')).toHaveTextContent(RELIABILITY_DISCLAIMER);
  });

  it('groups components by layer with value, points/max and source; missing rows are gray "нет данных"', () => {
    renderWithMantine(<ReliabilityScoreCard riskSignals={redCheapSignals} testIdSuffix="t" />);

    const layerA = screen.getByTestId('score-layer-A-t');
    expect(within(layerA).getByText('Структура выпуска')).toBeInTheDocument();
    const listing = within(layerA).getByTestId('score-component-listing-t');
    expect(listing).toHaveTextContent('листинг 1');
    expect(listing).toHaveTextContent('6 / 6');
    expect(within(layerA).getByTestId('score-component-broker_risk_level-t')).toHaveTextContent('T-Invest: risk_level');

    const layerB = screen.getByTestId('score-layer-B-t');
    expect(within(layerB).getByText('Отчётность эмитента')).toBeInTheDocument();
    const coverage = within(layerB).getByTestId('score-component-interest_coverage-t');
    expect(coverage).toHaveTextContent('2200/2330 = 2.4×');
    expect(coverage).toHaveTextContent('5 / 10');
    expect(coverage).toHaveTextContent('ГИР БО ФНС');
    expect(within(layerB).getByTestId('score-component-interest_coverage-t-stale')).toHaveTextContent('отчётность устарела');

    const missing = within(layerB).getByTestId('score-component-altman_z_em-t');
    expect(missing).toHaveAttribute('data-missing', 'true');
    expect(missing).toHaveTextContent('нет данных');
    expect(missing).toHaveTextContent('— / 8'); // Missing не равен нулю баллов

    expect(within(screen.getByTestId('score-layer-C-t')).getByText('Рынок')).toBeInTheDocument();
  });

  it('keeps the Value axis separate from the score: Red light and Cheap badge render together', () => {
    renderWithMantine(<ReliabilityScoreCard riskSignals={redCheapSignals} testIdSuffix="t" />);

    expect(screen.getByTestId('score-card-light-t')).toHaveAttribute('data-reliability', 'Red');
    const value = screen.getByTestId('value-badge-card-t');
    expect(value).toHaveAttribute('data-verdict', 'Cheap');
    expect(screen.getByTestId('score-card-value-t')).toHaveTextContent('не входит в балл');
  });

  it('shows an unknown flag key as-is instead of dropping it', () => {
    renderWithMantine(
      <ReliabilityScoreCard riskSignals={{ ...redCheapSignals, criticalFlags: ['BrandNewFlag'] }} testIdSuffix="t" />,
    );
    expect(screen.getByTestId('score-flag-critical-BrandNewFlag')).toHaveTextContent('BrandNewFlag');
  });
});

describe('Risk and Value badges render independently', () => {
  it('Red + Cheap and Green + Fair show two separate badges each', () => {
    renderWithMantine(
      <>
        <RiskSignalBadges signals={redCheapSignals} testIdSuffix="a" />
        <RiskSignalBadges signals={greenFairSignals} testIdSuffix="b" />
      </>,
    );

    expect(screen.getByTestId('reliability-dot-a')).toHaveAttribute('data-reliability', 'Red');
    expect(screen.getByTestId('value-badge-a')).toHaveAttribute('data-verdict', 'Cheap');
    expect(screen.getByTestId('value-badge-a')).toHaveTextContent('дёшево');
    expect(screen.getByTestId('reliability-dot-b')).toHaveAttribute('data-reliability', 'Green');
    expect(screen.getByTestId('value-badge-b')).toHaveAttribute('data-verdict', 'Fair');
  });

  it('renders "мало данных" for InsufficientData', () => {
    renderWithMantine(<RiskSignalBadges signals={{ ...greenFairSignals, valueVerdict: 'InsufficientData' }} testIdSuffix="c" />);
    expect(screen.getByTestId('value-badge-c')).toHaveTextContent('мало данных');
  });

  it('value tooltip explains "G-спред X против медианы корзины Y (порог ±Z)" in basis points', async () => {
    const user = userEvent.setup();
    renderWithMantine(<RiskSignalBadges signals={redCheapSignals} testIdSuffix="a" />);

    await user.hover(screen.getByTestId('value-badge-a'));
    const tooltip = await screen.findByTestId('value-tooltip-a');
    await waitFor(() => expect(tooltip).toBeVisible());
    // 0.04 / 0.03 / 0.002 — ДОЛИ → 400 б.п. / 300 б.п. / ±20 б.п.
    expect(tooltip).toHaveTextContent('G-спред 400 б.п. против медианы корзины 300 б.п. (порог ±20 б.п.)');
  });

  it('light tooltip says "балл N из 100, подробности по клику"', async () => {
    const user = userEvent.setup();
    renderWithMantine(<RiskSignalBadges signals={redCheapSignals} testIdSuffix="a" />);

    await user.hover(screen.getByTestId('reliability-dot-a'));
    const tooltip = await screen.findByTestId('reliability-tooltip-a');
    await waitFor(() => expect(tooltip).toBeVisible());
    expect(tooltip).toHaveTextContent('Балл 42 из 100, подробности по клику');
  });
});

describe('ReliabilityDot details popover (desktop)', () => {
  it('opens the full breakdown on click — assertions run AFTER expansion', async () => {
    const user = userEvent.setup();
    renderWithMantine(<RiskSignalBadges signals={redCheapSignals} testIdSuffix="a" />);

    expect(screen.queryByTestId('reliability-popover-a')).not.toBeInTheDocument();
    await user.click(screen.getByTestId('reliability-dot-a'));

    const popover = await screen.findByTestId('reliability-popover-a');
    await waitFor(() => expect(popover).toBeVisible());
    expect(within(popover).getByTestId('score-component-listing-a')).toBeVisible();
    expect(within(popover).getByTestId('score-component-altman_z_em-a')).toHaveTextContent('нет данных');
    expect(within(popover).getByTestId('score-flag-critical-InterestUncovered')).toBeVisible();
    expect(COMPONENTS.length).toBeGreaterThan(0);
  });

  it('lazily fetches the full card for a summary-only row (screener) on first open', async () => {
    const user = userEvent.setup();
    let requested: string | null = null;
    server.use(
      http.get('*/api/universe/:secid/reliability', ({ params }) => {
        requested = String(params.secid);
        return HttpResponse.json(reliabilityResponse());
      }),
    );
    renderWithMantine(
      <ReliabilityDot
        reliability="Red"
        reliabilityReason="r"
        testIdSuffix="row"
        score={41.6}
        details={{ lookup: 'RU000GAZP01' }}
      />,
    );

    expect(requested).toBeNull();
    await user.click(screen.getByTestId('reliability-dot-row'));

    const popover = await screen.findByTestId('reliability-popover-row');
    const listing = await within(popover).findByTestId('score-component-listing-row');
    await waitFor(() => expect(listing).toBeVisible());
    expect(requested).toBe('RU000GAZP01');
  });

  it('shows an inline error when the full card cannot be loaded', async () => {
    const user = userEvent.setup();
    renderWithMantine(
      <ReliabilityDot reliability="Red" reliabilityReason="причина" testIdSuffix="row" details={{ lookup: 'NOPE' }} />,
    );

    await user.click(screen.getByTestId('reliability-dot-row'));
    const error = await screen.findByTestId('reliability-details-error-row');
    await waitFor(() => expect(error).toBeVisible());
  });
});

describe('ReliabilityDot details drawer (mobile)', () => {
  const original = window.matchMedia;
  afterEach(() => {
    window.matchMedia = original;
  });

  it('opens the card in a Drawer below 48em', async () => {
    window.matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }));
    const user = userEvent.setup();
    const signals: RiskSignals = redCheapSignals;
    renderWithMantine(<RiskSignalBadges signals={signals} testIdSuffix="m" />);

    await user.click(screen.getByTestId('reliability-dot-m'));

    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog).toBeVisible());
    expect(await screen.findByTestId('reliability-score-card-m')).toBeVisible();
    expect(screen.queryByTestId('reliability-popover-m')).not.toBeInTheDocument();
  });
});
