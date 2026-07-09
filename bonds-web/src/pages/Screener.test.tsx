import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { Screener } from './Screener';
import { useScreenerStore } from '../store/useScreenerStore';
import type { UniverseRow, UniverseResponse, UniverseStatus } from '../api/types';

function renderScreener() {
  return render(
    <MantineProvider>
      <Screener />
    </MantineProvider>,
  );
}

const gazpromRow: UniverseRow = {
  secid: 'RU000GAZP01',
  isin: 'RU000GAZP001',
  name: 'Газпром капитал 5',
  sector: 'Корпоративные',
  yieldFraction: 0.19,
  durationYears: 1.8,
  pricePercent: 97,
  turnoverRub: 5_000_000,
  listLevel: 1,
  liquidityScore: 'High',
  slippageEstimateFraction: 0.001,
  gspreadApproxFraction: 0.03,
  maturityDate: '2028-01-01',
  offerDate: null,
  isHidden: false,
  hiddenReason: null,
  inPortfolio: false,
  inWatchlist: false,
};

const hiddenRow: UniverseRow = {
  ...gazpromRow,
  secid: 'RU000HIDDEN1',
  isin: 'RU000HIDDEN01',
  name: 'Неликвидный выпуск',
  liquidityScore: 'Low',
  isHidden: true,
  hiddenReason: 'LowTurnover',
};

const floaterRow: UniverseRow = {
  ...gazpromRow,
  secid: 'RU000FLOAT01',
  isin: 'RU000FLOAT001',
  name: 'Флоатер РЖД',
  isFloater: true,
};

const portfolioRow: UniverseRow = {
  ...gazpromRow,
  secid: 'RU000PORT001',
  isin: 'RU000PORT0011',
  name: 'Уже в портфеле',
  inPortfolio: true,
  inWatchlist: true,
};

const statusResponse: UniverseStatus = {
  lastRefreshUtc: '2026-07-07T10:00:00Z',
  totalBonds: 500,
  hiddenBonds: 42,
  historyDays: 30,
};

function universeResponse(rows: UniverseRow[], overrides?: Partial<UniverseResponse>): UniverseResponse {
  return {
    rows,
    total: rows.length,
    hiddenCount: rows.filter((r) => r.isHidden).length,
    disclaimer: 'Метрики банка облигаций — биржевая статистика MOEX, не инвестиционная рекомендация.',
    ...overrides,
  };
}

beforeEach(() => {
  useScreenerStore.setState({
    filters: {
      search: '',
      minYield: null,
      maxYield: null,
      minDurationYears: null,
      maxDurationYears: null,
      sector: null,
      includeHidden: false,
      fixedCouponOnly: false,
    },
    sortBy: 'yield',
    sortDir: 'desc',
    offset: 0,
    rows: [],
    total: 0,
    hiddenCount: 0,
    disclaimer: '',
    isLoading: false,
    error: null,
    status: null,
    isStatusLoading: false,
  });

  server.use(
    http.get('*/api/universe/status', () => HttpResponse.json(statusResponse)),
    http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow, hiddenRow.isHidden ? undefined : hiddenRow].filter(Boolean) as UniverseRow[]))),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Screener', () => {
  it('renders the status bar from GET /api/universe/status', async () => {
    server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow]))));
    renderScreener();

    await waitFor(() => expect(screen.getByTestId('screener-status-bar')).toBeInTheDocument());
    expect(screen.getByTestId('screener-status-bar').textContent).toContain('500');
  });

  it('renders bond rows in the desktop table', async () => {
    server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow]))));
    renderScreener();

    await waitFor(() => expect(screen.getByTestId('screener-table')).toBeInTheDocument());
    expect(screen.getByTestId(`screener-row-${gazpromRow.secid}`)).toBeInTheDocument();
    expect(screen.getByText('Газпром капитал 5')).toBeInTheDocument();
  });

  it('sends filter parameters to GET /api/universe when a filter changes', async () => {
    let lastUrl: string | null = null;
    server.use(
      http.get('*/api/universe', ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json(universeResponse([gazpromRow]));
      }),
    );
    renderScreener();

    await waitFor(() => expect(screen.getByTestId('screener-table')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('screener-min-yield'), { target: { value: '10' } });

    await waitFor(() => {
      expect(lastUrl).toContain('minYield=0.1');
    });
  });

  it('sends search text to GET /api/universe (debounced)', async () => {
    let lastUrl: string | null = null;
    server.use(
      http.get('*/api/universe', ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json(universeResponse([gazpromRow]));
      }),
    );
    renderScreener();

    await waitFor(() => expect(screen.getByTestId('screener-table')).toBeInTheDocument());

    fireEvent.change(screen.getByTestId('screener-search-input'), { target: { value: 'Газпром' } });

    await waitFor(
      () => {
        expect(lastUrl).toContain('search=%D0%93%D0%B0%D0%B7%D0%BF%D1%80%D0%BE%D0%BC');
      },
      { timeout: 1000 },
    );
  });

  it('toggles sort direction when clicking a sortable column header', async () => {
    const urls: string[] = [];
    server.use(
      http.get('*/api/universe', ({ request }) => {
        urls.push(request.url);
        return HttpResponse.json(universeResponse([gazpromRow]));
      }),
    );
    renderScreener();

    await waitFor(() => expect(screen.getByTestId('screener-table')).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('screener-sort-duration'));

    await waitFor(() => {
      const last = urls[urls.length - 1];
      expect(last).toContain('sortBy=duration');
      expect(last).toContain('sortDir=desc');
    });

    fireEvent.click(screen.getByTestId('screener-sort-duration'));

    await waitFor(() => {
      const last = urls[urls.length - 1];
      expect(last).toContain('sortBy=duration');
      expect(last).toContain('sortDir=asc');
    });
  });

  it('shows hidden rows with a reason badge when "показать скрытые" is toggled on', async () => {
    server.use(
      http.get('*/api/universe', ({ request }) => {
        const url = new URL(request.url);
        const includeHidden = url.searchParams.get('includeHidden') === 'true';
        return HttpResponse.json(universeResponse(includeHidden ? [gazpromRow, hiddenRow] : [gazpromRow]));
      }),
    );
    renderScreener();

    await waitFor(() => expect(screen.getByTestId('screener-table')).toBeInTheDocument());
    expect(screen.queryByTestId(`screener-row-${hiddenRow.secid}`)).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('screener-include-hidden-toggle'));

    await waitFor(() => expect(screen.getByTestId(`screener-row-${hiddenRow.secid}`)).toBeInTheDocument());
    expect(screen.getByTestId(`screener-hidden-badge-${hiddenRow.secid}`).textContent).toContain('низкий оборот');
  });

  it('shows ownership badges for rows already in the portfolio/watchlist', async () => {
    server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([portfolioRow]))));
    renderScreener();

    await waitFor(() => expect(screen.getByTestId(`screener-in-portfolio-${portfolioRow.secid}`)).toBeInTheDocument());
    expect(screen.getByTestId(`screener-in-watchlist-${portfolioRow.secid}`)).toBeInTheDocument();
  });

  it('adds a bond to the watchlist and updates the badge optimistically', async () => {
    let postedIsin: string | null = null;
    server.use(
      http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow]))),
      http.post('*/api/watchlist', async ({ request }) => {
        const body = (await request.json()) as { isin: string };
        postedIsin = body.isin;
        return HttpResponse.json({ id: 1, isin: body.isin, note: null, addedAtUtc: new Date().toISOString() }, { status: 201 });
      }),
    );
    renderScreener();

    await waitFor(() => expect(screen.getByTestId(`screener-add-watchlist-${gazpromRow.secid}`)).toBeInTheDocument());
    fireEvent.click(screen.getByTestId(`screener-add-watchlist-${gazpromRow.secid}`));

    await waitFor(() => expect(screen.getByTestId(`screener-in-watchlist-${gazpromRow.secid}`)).toBeInTheDocument());
    expect(postedIsin).toBe(gazpromRow.isin);
  });

  // ─── Задача 32 часть B: пометка флоатера + фильтр «только фикс-купон» ─────────────────────

  it('shows a floater badge instead of a YTM number for floater rows', async () => {
    server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow, floaterRow]))));
    renderScreener();

    await waitFor(() => expect(screen.getByTestId(`screener-row-${floaterRow.secid}`)).toBeInTheDocument());

    const floaterCell = screen.getByTestId(`screener-row-${floaterRow.secid}`);
    expect(within(floaterCell).getByTestId(`screener-floater-badge-${floaterRow.secid}`)).toBeInTheDocument();
    expect(within(floaterCell).getByTestId(`screener-floater-badge-${floaterRow.secid}`).textContent).toContain(
      'плав. купон',
    );

    // Фикс-купонная строка (isFloater: false) продолжает показывать доходность числом, не бейджем.
    const fixedCell = screen.getByTestId(`screener-row-${gazpromRow.secid}`);
    expect(within(fixedCell).queryByTestId(`screener-floater-badge-${gazpromRow.secid}`)).not.toBeInTheDocument();
    expect(fixedCell.textContent).toContain('19.00%');
  });

  it('keeps floaters visible by default and hides them once "только фикс-купон" is toggled on', async () => {
    server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow, floaterRow]))));
    renderScreener();

    await waitFor(() => expect(screen.getByTestId(`screener-row-${floaterRow.secid}`)).toBeInTheDocument());

    fireEvent.click(screen.getByTestId('screener-fixed-coupon-toggle'));

    await waitFor(() =>
      expect(screen.queryByTestId(`screener-row-${floaterRow.secid}`)).not.toBeInTheDocument(),
    );
    // Фикс-купонная строка остаётся видна.
    expect(screen.getByTestId(`screener-row-${gazpromRow.secid}`)).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('screener-fixed-coupon-toggle'));
    await waitFor(() => expect(screen.getByTestId(`screener-row-${floaterRow.secid}`)).toBeInTheDocument());
  });

  it('treats isFloater == null/undefined as "not a floater" — row stays visible, no badge', async () => {
    const unknownRow: UniverseRow = { ...gazpromRow, secid: 'RU000UNKNOWN', isin: 'RU000UNKNOWN1', isFloater: null };
    server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([unknownRow]))));
    renderScreener();

    await waitFor(() => expect(screen.getByTestId(`screener-row-${unknownRow.secid}`)).toBeInTheDocument());
    const row = screen.getByTestId(`screener-row-${unknownRow.secid}`);
    expect(within(row).queryByTestId(`screener-floater-badge-${unknownRow.secid}`)).not.toBeInTheDocument();
    expect(row.textContent).toContain('19.00%');

    fireEvent.click(screen.getByTestId('screener-fixed-coupon-toggle'));
    await waitFor(() => expect(screen.getByTestId(`screener-fixed-coupon-toggle`)).toBeChecked());
    expect(screen.getByTestId(`screener-row-${unknownRow.secid}`)).toBeInTheDocument();
  });

  describe('mobile layout', () => {
    beforeEach(() => {
      // jsdom matchMedia стаб по умолчанию (src/test/setup.ts) всегда matches:false — переопределяем
      // на "матчит query <=48em", тот же паттерн, что могли бы использовать другие мобильные тесты.
      vi.stubGlobal('matchMedia', (query: string) => ({
        matches: query.includes('max-width: 48em'),
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      }));
    });

    it('renders cards instead of a table on mobile, with filters collapsed', async () => {
      server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse([gazpromRow]))));
      renderScreener();

      await waitFor(() => expect(screen.getByTestId('screener-cards')).toBeInTheDocument());
      expect(screen.queryByTestId('screener-table')).not.toBeInTheDocument();
      expect(screen.getByTestId(`screener-card-${gazpromRow.secid}`)).toBeInTheDocument();
      // Панель фильтров скрыта за Collapse по умолчанию на мобиле (Mantine Collapse не размонтирует
      // содержимое — измеряем видимость, а не присутствие в DOM, тот же паттерн, что PositionCard).
      expect(screen.getByTestId('screener-search-input')).not.toBeVisible();

      fireEvent.click(screen.getByTestId('screener-filters-toggle'));
      await waitFor(() => expect(screen.getByTestId('screener-search-input')).toBeVisible());
    });

    it('shows a "показать ещё" button when there are more rows than the visible batch', async () => {
      const manyRows = Array.from({ length: 60 }, (_, i) => ({
        ...gazpromRow,
        secid: `RU000ROW${i.toString().padStart(3, '0')}`,
        isin: `RU000ROW${i.toString().padStart(3, '0')}1`,
        name: `Бумага ${i}`,
      }));
      server.use(http.get('*/api/universe', () => HttpResponse.json(universeResponse(manyRows))));
      renderScreener();

      await waitFor(() => expect(screen.getByTestId('screener-cards')).toBeInTheDocument());
      expect(screen.getByTestId('screener-show-more')).toBeInTheDocument();

      fireEvent.click(screen.getByTestId('screener-show-more'));
      await waitFor(() => expect(screen.queryByTestId('screener-show-more')).not.toBeInTheDocument());
    });
  });
});
