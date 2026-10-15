import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { ReplacementBreakdown } from './ReplacementBreakdown';
import { horizonCaption, toBreakdownData } from '../utils/replacementBreakdown';
import { replacementResponseFixture } from '../test/replacementFixtures';
import type { ReplacementResponse } from '../api/types';

function renderBreakdown(overrides: Partial<ReplacementResponse> = {}) {
  return render(
    <MantineProvider>
      <ReplacementBreakdown data={toBreakdownData(replacementResponseFixture(overrides))} testIdSuffix="t" />
    </MantineProvider>,
  );
}

describe('ReplacementBreakdown (задача 48)', () => {
  it('renders after-tax yields of both legs (НДФЛ на купоны), slippage of both legs and the effective horizon', () => {
    renderBreakdown();

    const yields = screen.getByTestId('replacement-yield-after-tax-t').textContent!;
    expect(yields).toMatch(/доходность после НДФЛ 13\.00%/);
    expect(yields).toMatch(/текущая 8\.70%/);
    expect(yields).toMatch(/цель 13\.92%/);

    const slippage = screen.getByTestId('replacement-slippage-t').textContent!;
    expect(slippage).toMatch(/слиппедж продажи/);
    expect(slippage).toMatch(/слиппедж покупки/);

    expect(screen.getByTestId('replacement-horizon-t').textContent).toBe('горизонт: 2.0 г.');
    expect(screen.queryByTestId('replacement-slippage-unknown-t')).not.toBeInTheDocument();
    expect(screen.queryByTestId('replacement-duration-warning-t')).not.toBeInTheDocument();
  });

  it('explains the horizon cut by the target offer', () => {
    renderBreakdown({ effectiveHorizonYears: 0.8, horizonLimitedByTarget: true, targetHorizonDate: '2027-05-01', targetHorizonIsOffer: true });

    expect(screen.getByTestId('replacement-horizon-t').textContent).toMatch(/горизонт: 10 мес \(сокращён с 2\.0 г\.: оферта цели 01\.05\.2027\)/);
  });

  it('marks unknown order book: "спред стакана неизвестен, слиппедж не учтён" and hides the zero slippage line', () => {
    renderBreakdown({ slippageUnknown: true, sellSlippageRub: 0, buySlippageRub: 0 });

    expect(screen.getByTestId('replacement-slippage-unknown-t').textContent).toMatch(/спред стакана неизвестен, слиппедж не учтён/);
    expect(screen.queryByTestId('replacement-slippage-t')).not.toBeInTheDocument();
  });

  it('keeps the slippage line when only one leg is unknown', () => {
    renderBreakdown({ slippageUnknown: true, sellSlippageRub: 20, buySlippageRub: 0 });

    expect(screen.getByTestId('replacement-slippage-unknown-t').textContent).toMatch(/слиппедж одной из сторон не учтён/);
    expect(screen.getByTestId('replacement-slippage-t')).toBeInTheDocument();
  });

  it('shows the duration mismatch warning from the backend', () => {
    renderBreakdown({ durationMismatch: true, durationMismatchWarning: 'Процентный риск изменится: дюрация 1.2 → 3.0 лет' });

    expect(screen.getByTestId('replacement-duration-warning-t').textContent).toBe('Процентный риск изменится: дюрация 1.2 → 3.0 лет');
  });

  it('keeps the sale-tax rows (existing behaviour) and the unavailable-tax note', () => {
    renderBreakdown();
    expect(screen.getByTestId('replacement-sell-tax-t')).toBeInTheDocument();
    expect(screen.getByTestId('replacement-net-after-tax-t')).toBeInTheDocument();
  });

  it('shows the "tax not estimated" note when the journal is incomplete', () => {
    renderBreakdown({ sellTaxEstimateRub: null, netBenefitAfterTaxRub: null });
    expect(screen.getByTestId('replacement-tax-unavailable-t')).toBeInTheDocument();
  });
});

describe('horizonCaption', () => {
  const base = {
    horizonYears: 2,
    horizonSource: 'Settings' as const,
    effectiveHorizonYears: 2,
    horizonLimitedByTarget: false,
    targetHorizonDate: null,
    targetHorizonIsOffer: false,
  };

  it('says "на горизонте N лет (настройка)" with Russian plural forms', () => {
    expect(horizonCaption(base)).toBe('на горизонте 2 года (настройка)');
    expect(horizonCaption({ ...base, horizonYears: 5 })).toBe('на горизонте 5 лет (настройка)');
    expect(horizonCaption({ ...base, horizonYears: 1 })).toBe('на горизонте 1 год (настройка)');
    expect(horizonCaption({ ...base, horizonYears: 0.5 })).toBe('на горизонте 0.5 года (настройка)');
    expect(horizonCaption({ ...base, horizonYears: 10 })).toBe('на горизонте 10 лет (настройка)');
  });

  it('labels an explicit request horizon differently from the setting', () => {
    expect(horizonCaption({ ...base, horizonSource: 'Request' })).toBe('на горизонте 2 года (запрос)');
  });
});
