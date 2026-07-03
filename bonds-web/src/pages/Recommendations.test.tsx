import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Recommendations } from './Recommendations';
import { useRecommendationsStore } from '../store/useRecommendationsStore';
import { useAuthStore } from '../store/useAuthStore';
import type { ComparisonResponse, ReplacementResponse, AllocationResponse, CompositionResponse } from '../api/types';

function renderRecommendations() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Recommendations />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const baseComposition: CompositionResponse = {
  totalMarketValueRub: 200000,
  byIssuer: [
    { key: 'Слабый Эмитент', marketValueRub: 100000, sharePercent: 50 },
    { key: 'Хороший Эмитент', marketValueRub: 100000, sharePercent: 50 },
  ],
  bySector: [],
  byCouponType: [],
  byDurationBucket: [],
  disclaimer: '',
};

const weakRow = {
  positionId: 1,
  instrumentId: 10,
  name: 'Слабая бумага',
  issuer: 'Слабый Эмитент',
  effectiveYield: 0.08,
  yieldKind: 'Ytm' as const,
  modifiedDuration: 2,
  gSpread: 50,
  daysToHorizon: 30,
  horizonDate: '2026-08-02',
  calculatedToOffer: false,
  couponType: 'Fixed' as const,
  isEstimated: false,
  dataIncomplete: false,
};

const strongRow = {
  positionId: 2,
  instrumentId: 11,
  name: 'Сильная бумага',
  issuer: 'Хороший Эмитент',
  effectiveYield: 0.16,
  yieldKind: 'Ytm' as const,
  modifiedDuration: 2.2,
  gSpread: 150,
  daysToHorizon: 900,
  horizonDate: '2028-12-01',
  calculatedToOffer: false,
  couponType: 'Fixed' as const,
  isEstimated: false,
  dataIncomplete: false,
};

const floaterRow = {
  positionId: 3,
  instrumentId: 12,
  name: 'Флоатер',
  issuer: 'РЖД',
  effectiveYield: 0.1,
  yieldKind: 'Current' as const,
  modifiedDuration: null,
  gSpread: null,
  daysToHorizon: 500,
  horizonDate: '2027-11-01',
  calculatedToOffer: false,
  couponType: 'Floating' as const,
  isEstimated: true,
  dataIncomplete: false,
};

const baseComparison: ComparisonResponse = {
  rows: [weakRow, strongRow, floaterRow],
  disclaimer: 'Сортировка по доходности не учитывает срок до погашения/оферты и кредитный риск эмитента.',
};

const favorableReplacement: ReplacementResponse = {
  holdPositionId: 1,
  targetPositionId: 2,
  horizonYears: 2,
  sellCommissionRub: 30,
  buyCommissionRub: 30,
  totalSwitchCostRub: 60,
  netBenefitRub: 1500,
  isSwitchFavorable: true,
  breakEvenYears: 0.2,
  yieldDataIncomplete: false,
  disclaimer: 'Анализ замены сравнивает только текущие позиции портфеля.',
};

const allocationResult: AllocationResponse = {
  amountRub: 15000,
  allocations: [
    {
      instrumentId: 11,
      name: 'Сильная бумага',
      issuer: 'Хороший Эмитент',
      quantity: 10,
      estimatedCostRub: 10000,
      effectiveYield: 0.16,
      lotSizeAssumed: true,
    },
  ],
  skipped: [],
  leftoverRub: 5000,
  disclaimer: 'Оценка распределения свободных средств по бумагам текущего портфеля. Не учитывает налоги и не является инвестиционной рекомендацией.',
};

describe('Recommendations', () => {
  beforeEach(() => {
    useRecommendationsStore.setState({
      sellCandidates: [],
      outOfComparison: [],
      comparisonDisclaimer: '',
      replacements: [],
      isLoading: false,
      error: null,
      allocationAmount: 15000,
      allocation: null,
      isAllocationLoading: false,
      allocationError: null,
    });
    useAuthStore.setState({ token: 'test-token', user: { id: 1, telegramId: 1 } });

    server.use(
      http.get('*/api/analytics/composition', () => HttpResponse.json(baseComposition)),
      http.get('*/api/analytics/comparison', () => HttpResponse.json(baseComparison)),
      http.post('*/api/analytics/replacement', () => HttpResponse.json(favorableReplacement)),
      http.get('*/api/analytics/allocation', () => HttpResponse.json(allocationResult)),
    );
  });

  it('renders all three sections', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-section')).toBeInTheDocument());
    expect(screen.getByTestId('replacements-section')).toBeInTheDocument();
    expect(screen.getByTestId('allocation-section')).toBeInTheDocument();
  });

  it('shows a sell candidate with reason badges', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('sell-candidate-1')).toBeInTheDocument());
    const card = screen.getByTestId('sell-candidate-1');
    expect(card.textContent).toMatch(/медианы портфеля/);
    expect(card.textContent).toMatch(/погашение через 30 дн\./);
  });

  it('puts the floater in the "out of comparison" list, not ranked with YTM positions', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('out-of-comparison-3')).toBeInTheDocument());
    expect(screen.queryByTestId('sell-candidate-3')).not.toBeInTheDocument();
  });

  it('shows a favorable replacement card with benefit and break-even', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('replacement-1-2')).toBeInTheDocument());
    expect(screen.getByTestId('replacement-1-2').textContent).toMatch(/выгода/);
  });

  it('allocation form shows the result and leftover after submit', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-result')).toBeInTheDocument());
    expect(screen.getByTestId('allocation-line-11')).toBeInTheDocument();
    expect(screen.getByTestId('allocation-leftover').textContent).toMatch(/5[\s\u00a0]000/);
  });

  it('renders a disclaimer on the page', async () => {
    renderRecommendations();

    await waitFor(() => expect(screen.getAllByTestId('disclaimer').length).toBeGreaterThan(0));
  });

  it('shows an empty allocation state when nothing matches the amount', async () => {
    server.use(
      http.get('*/api/analytics/allocation', () =>
        HttpResponse.json({ amountRub: 100, allocations: [], skipped: [], leftoverRub: 100, disclaimer: '' }),
      ),
    );

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('allocation-empty')).toBeInTheDocument());
  });

  it('shows an error state without crashing when comparison fails', async () => {
    server.use(http.get('*/api/analytics/comparison', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderRecommendations();

    await waitFor(() => expect(screen.getByTestId('weak-links-error')).toBeInTheDocument());
  });
});
