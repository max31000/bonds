import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { PortfolioIntradayChart } from './PortfolioIntradayChart';
import { useLiveStore } from '../store/useLiveStore';

function renderChart() {
  return render(
    <MantineProvider>
      <PortfolioIntradayChart />
    </MantineProvider>,
  );
}

describe('PortfolioIntradayChart', () => {
  beforeEach(() => {
    useLiveStore.setState({ positionsById: {}, totalMarketValueRub: null, asOfUtc: null });
  });

  it('shows an empty state when there are no points yet', async () => {
    server.use(http.get('*/api/live/portfolio-intraday', () => HttpResponse.json({ points: [] })));

    renderChart();

    await waitFor(() => expect(screen.getByTestId('intraday-chart-empty')).toBeInTheDocument());
  });

  it('renders the chart once points arrive', async () => {
    server.use(
      http.get('*/api/live/portfolio-intraday', () =>
        HttpResponse.json({
          points: [
            { tsUtc: '2026-07-03T07:00:00Z', totalMarketValueRub: 100000 },
            { tsUtc: '2026-07-03T07:01:00Z', totalMarketValueRub: 100500 },
          ],
        }),
      ),
    );

    renderChart();

    await waitFor(() => expect(screen.queryByTestId('intraday-chart-empty')).not.toBeInTheDocument());
    expect(screen.queryByTestId('intraday-chart-error')).not.toBeInTheDocument();
  });

  it('shows an error message when the request fails', async () => {
    server.use(
      http.get('*/api/live/portfolio-intraday', () => HttpResponse.json({ error: 'boom' }, { status: 500 })),
    );

    renderChart();

    await waitFor(() => expect(screen.getByTestId('intraday-chart-error')).toBeInTheDocument());
  });

  it('re-requests the series with the selected range when the toggle changes', async () => {
    const seenRanges: string[] = [];
    server.use(
      http.get('*/api/live/portfolio-intraday', ({ request }) => {
        const url = new URL(request.url);
        seenRanges.push(url.searchParams.get('range') ?? '1d');
        return HttpResponse.json({ points: [] });
      }),
    );

    renderChart();
    await waitFor(() => expect(seenRanges).toContain('1d'));

    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByText('5д'));

    await waitFor(() => expect(seenRanges).toContain('5d'));
  });
});
