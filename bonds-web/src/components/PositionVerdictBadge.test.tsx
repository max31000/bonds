import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { PositionVerdictBadge, PositionVerdictBlock } from './PositionVerdictBadge';
import { VERDICT_META } from './positionVerdictCopy';
import type { PositionVerdict, PositionVerdictKind } from '../api/types';

function verdictOf(kind: PositionVerdictKind, overrides: Partial<PositionVerdict> = {}): PositionVerdict {
  return {
    positionId: 7,
    instrumentId: 70,
    verdict: kind,
    reasons: ['Первая причина', 'Вторая причина', 'Третья причина'],
    confidence: 'High',
    bestReplacement: null,
    ...overrides,
  };
}

function wrap(node: React.ReactNode) {
  return render(
    <MantineProvider>
      <MemoryRouter>{node}</MemoryRouter>
    </MantineProvider>,
  );
}

describe('PositionVerdictBadge', () => {
  it.each<[PositionVerdictKind, string]>([
    ['Hold', 'Держать'],
    ['BuyMoreCandidate', 'Кандидат на докупку'],
    ['ConsiderReplace', 'Рассмотреть замену'],
    ['ConsiderExit', 'Рассмотреть выход'],
  ])('renders the %s verdict with its label', (kind, label) => {
    wrap(<PositionVerdictBadge verdict={verdictOf(kind)} />);

    const badge = screen.getByTestId('position-verdict-7');
    expect(badge).toHaveTextContent(label);
    expect(badge).toHaveAttribute('data-verdict', kind);
  });

  it('uses neutral colors: gray hold, blue buy-more, yellow replace, red exit', () => {
    expect(VERDICT_META.Hold.color).toBe('gray');
    expect(VERDICT_META.BuyMoreCandidate.color).toBe('blue');
    expect(VERDICT_META.ConsiderReplace.color).toBe('yellow');
    expect(VERDICT_META.ConsiderExit.color).toBe('red');
  });

  it('never uses imperative buy/sell wording in labels', () => {
    for (const { label } of Object.values(VERDICT_META)) {
      expect(label.toLowerCase()).not.toMatch(/купите|продайте|покупайте|продавайте/);
    }
  });

  it('shows the reasons (max three) and "not advice" caption in the tooltip on hover', async () => {
    wrap(<PositionVerdictBadge verdict={verdictOf('ConsiderExit')} />);

    await userEvent.hover(screen.getByTestId('position-verdict-7'));

    await waitFor(() => expect(screen.getByText('Первая причина')).toBeInTheDocument());
    expect(screen.getByText('Вторая причина')).toBeInTheDocument();
    expect(screen.getByText('Третья причина')).toBeInTheDocument();
    expect(screen.getByText(/оценка, не рекомендация/)).toBeInTheDocument();
  });

  it('renders nothing when the verdict is not available (not loaded / request failed)', () => {
    const { container } = wrap(<PositionVerdictBadge verdict={undefined} />);

    expect(screen.queryByTestId('position-verdict-7')).not.toBeInTheDocument();
    expect(container.querySelector('[data-verdict]')).toBeNull();
  });
});

describe('PositionVerdictBlock', () => {
  it('lists reasons, confidence, the disclaimer and a link to the recommendations with the selected position', () => {
    wrap(<PositionVerdictBlock verdict={verdictOf('ConsiderReplace', { confidence: 'Medium' })} disclaimer="Тестовый дисклеймер вердикта." />);

    const reasons = screen.getByTestId('position-verdict-reasons');
    expect(reasons).toHaveTextContent('Первая причина');
    expect(reasons).toHaveTextContent('Третья причина');
    expect(screen.getByTestId('position-verdict-block-badge')).toHaveTextContent('Рассмотреть замену');
    expect(screen.getByTestId('position-verdict-confidence')).toHaveTextContent('средняя');
    expect(screen.getByTestId('position-verdict-disclaimer')).toHaveTextContent('Оценка, не рекомендация');
    expect(screen.getByTestId('position-verdict-disclaimer')).toHaveTextContent('Тестовый дисклеймер вердикта.');
    expect(screen.getByTestId('position-verdict-find-replacement')).toHaveAttribute('href', '/recommendations?positionId=7');
  });

  it('shows low confidence as "данных мало" and the best replacement with figures (fractions → b.p.)', () => {
    wrap(
      <PositionVerdictBlock
        verdict={verdictOf('ConsiderReplace', {
          confidence: 'Low',
          bestReplacement: {
            name: 'ОФЗ-Кандидат',
            secid: 'SU1',
            isin: 'RU1',
            targetReliability: 'Green',
            annualizedBenefitFraction: 0.0085,
            netBenefitAfterTaxRub: 1234,
            effectiveHorizonYears: 2,
          },
        })}
        disclaimer=""
      />,
    );

    expect(screen.getByTestId('position-verdict-confidence')).toHaveTextContent('данных мало');
    const best = screen.getByTestId('position-verdict-best-replacement');
    expect(best).toHaveTextContent('ОФЗ-Кандидат');
    expect(best).toHaveTextContent('85 б.п. годовых');
  });
});
