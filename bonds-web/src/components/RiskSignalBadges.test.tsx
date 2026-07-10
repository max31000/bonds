import { render, screen, within, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { RiskSignalBadges, RiskSignalsCaption, RELIABILITY_DISCLAIMER } from './RiskSignalBadges';
import type { RiskSignals } from '../api/types';

/** Задача 38 часть D.3 — рендер светофора + тултип (reliabilityReason + оба детальных сигнала +
 * обязательный дисклеймер) + существующие бейджи ликвидности/спреда не сломаны (аддитивность). */
function renderBadges(signals: RiskSignals, testIdSuffix = 'x') {
  return render(
    <MantineProvider>
      <RiskSignalBadges signals={signals} testIdSuffix={testIdSuffix} />
    </MantineProvider>,
  );
}

const greenSignals: RiskSignals = {
  liquidity: 'Good',
  liquidityLabel: 'Высокая ликвидность, листинг 1',
  spread: 'Neutral',
  gSpreadFraction: 0.03,
  spreadVsBasketMedianFraction: 0.001,
  reliability: 'Green',
  reliabilityReason: 'Зелёный: оба риск-сигнала в норме, листинг 1-2, ликвидность с данными.',
};

const redSignals: RiskSignals = {
  ...greenSignals,
  liquidity: 'Caution',
  liquidityLabel: 'Низкий оборот, листинг 3',
  reliability: 'Red',
  reliabilityReason: 'Красный: ликвидность/листинг в зоне риска (сигнал Caution).',
};

const yellowSignals: RiskSignals = {
  ...greenSignals,
  reliability: 'Yellow',
  reliabilityReason: 'Жёлтый: листинг неизвестен (для зелёного нужен листинг 1 или 2).',
};

describe('RiskSignalBadges — светофор надёжности (задача 38)', () => {
  it('renders the reliability dot alongside the existing liquidity/spread badges (additive, not replacing)', () => {
    renderBadges(greenSignals);

    expect(screen.getByTestId('reliability-dot-x')).toBeInTheDocument();
    expect(screen.getByTestId('risk-signal-liquidity-x')).toBeInTheDocument();
    expect(screen.getByTestId('risk-signal-spread-x')).toBeInTheDocument();
  });

  it.each([
    ['Green', greenSignals],
    ['Yellow', yellowSignals],
    ['Red', redSignals],
  ] as const)('marks the dot with data-reliability=%s', (level, signals) => {
    renderBadges(signals);

    expect(screen.getByTestId('reliability-dot-x')).toHaveAttribute('data-reliability', level);
  });

  it('shows reliabilityReason, both detail signals, and the mandatory "not a credit rating" disclaimer on hover', async () => {
    const user = userEvent.setup();
    renderBadges(redSignals);

    await user.hover(screen.getByTestId('reliability-dot-x'));

    const tooltip = await screen.findByTestId('reliability-tooltip-x');
    await waitFor(() => expect(tooltip).toBeVisible());
    expect(within(tooltip).getByText(redSignals.reliabilityReason)).toBeVisible();
    expect(within(tooltip).getByText(redSignals.liquidityLabel)).toBeVisible();
    expect(within(tooltip).getByText(RELIABILITY_DISCLAIMER)).toBeVisible();
  });

  it('never labels the traffic light a credit rating anywhere in the caption', () => {
    render(
      <MantineProvider>
        <RiskSignalsCaption />
      </MantineProvider>,
    );

    const caption = screen.getByTestId('risk-signals-caption');
    expect(caption.textContent).not.toMatch(/^рейтинг$/i);
    expect(caption.textContent).toContain('не рейтинг рейтинговых агентств');
  });
});
