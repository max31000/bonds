import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { server } from '../test/msw-handlers';
import { IssuerDataBlock } from './IssuerDataBlock';
import type { IssuerRating } from '../api/types';

const INN = '7736050003';

/** Дата относительно «сегодня» (правило гайда: никаких абсолютных дат в тестах). */
function daysAgoIso(days: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString().slice(0, 10);
}

function renderBlock(props: { defaultInn?: string | null; onChanged?: () => void } = {}) {
  return render(
    <MantineProvider>
      <IssuerDataBlock defaultInn={props.defaultInn ?? INN} onChanged={props.onChanged} />
    </MantineProvider>,
  );
}

async function openPanel(testId: string) {
  fireEvent.click(await screen.findByTestId(testId));
}

describe('IssuerDataBlock — рейтинги', () => {
  it('submits a rating, refetches the list and notifies the parent (scorecard refetch)', async () => {
    const stored: IssuerRating[] = [];
    let posted: unknown = null;
    server.use(
      http.get('*/api/issuers/:inn/ratings', ({ params }) =>
        HttpResponse.json({ inn: params.inn, ratings: stored, freshMonths: 18, disclaimer: '' }),
      ),
      http.post('*/api/issuers/:inn/ratings', async ({ request }) => {
        const body = (await request.json()) as { agency: string; rating: string; ratingDate: string; note: string | null };
        posted = body;
        stored.push({ agency: body.agency, rating: body.rating, ratingDate: body.ratingDate, note: body.note, isStale: false });
        return HttpResponse.json(stored[0]);
      }),
    );
    const onChanged = vi.fn();
    renderBlock({ onChanged });

    await openPanel('issuer-ratings-toggle');
    expect(await screen.findByTestId('issuer-ratings-empty')).toBeVisible();

    const ratingDate = daysAgoIso(30);
    fireEvent.change(screen.getByTestId('issuer-rating-value'), { target: { value: 'A(RU)' } });
    fireEvent.change(screen.getByTestId('issuer-rating-date'), { target: { value: ratingDate } });
    fireEvent.click(screen.getByTestId('issuer-rating-save'));

    const row = await screen.findByTestId('issuer-rating-АКРА');
    expect(row).toHaveTextContent('A(RU)');
    expect(posted).toMatchObject({ agency: 'АКРА', rating: 'A(RU)', ratingDate });
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it('shows the 422 validation message inline and does not notify the parent', async () => {
    server.use(
      http.post('*/api/issuers/:inn/ratings', () =>
        HttpResponse.json({ error: 'rating не распознан: ожидается шкала AAA/AA/A/BBB/BB/B/CCC/CC/C/D', type: 'ValidationException' }, { status: 422 }),
      ),
    );
    const onChanged = vi.fn();
    renderBlock({ onChanged });

    await openPanel('issuer-ratings-toggle');
    fireEvent.change(await screen.findByTestId('issuer-rating-value'), { target: { value: 'ZZZ' } });
    fireEvent.change(screen.getByTestId('issuer-rating-date'), { target: { value: daysAgoIso(5) } });
    fireEvent.click(screen.getByTestId('issuer-rating-save'));

    const error = await screen.findByTestId('issuer-rating-error');
    expect(error).toHaveTextContent('rating не распознан');
    expect(onChanged).not.toHaveBeenCalled();
  });

  it('deletes a rating via its row button', async () => {
    let ratings: IssuerRating[] = [{ agency: 'НКР', rating: 'BBB', ratingDate: daysAgoIso(60), note: null, isStale: true }];
    let deleted: string | null = null;
    server.use(
      http.get('*/api/issuers/:inn/ratings', ({ params }) =>
        HttpResponse.json({ inn: params.inn, ratings, freshMonths: 18, disclaimer: '' }),
      ),
      http.delete('*/api/issuers/:inn/ratings/:agency', ({ params }) => {
        deleted = decodeURIComponent(String(params.agency));
        ratings = [];
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const onChanged = vi.fn();
    renderBlock({ onChanged });

    await openPanel('issuer-ratings-toggle');
    const row = await screen.findByTestId('issuer-rating-НКР');
    expect(row).toHaveTextContent('не учитывается'); // isStale
    fireEvent.click(within(row).getByTestId('issuer-rating-delete-НКР'));

    await waitFor(() => expect(screen.getByTestId('issuer-ratings-empty')).toBeInTheDocument());
    expect(deleted).toBe('НКР');
    expect(onChanged).toHaveBeenCalledTimes(1);
  });
});

describe('IssuerDataBlock — отчётность', () => {
  it('sends amounts in RUBLES as typed (no thousands conversion), normalising spaces and decimal comma', async () => {
    let posted: Record<string, unknown> | null = null;
    server.use(
      http.post('*/api/issuers/:inn/financials', async ({ request }) => {
        posted = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ period: posted.period, source: 'manual' });
      }),
    );
    const onChanged = vi.fn();
    renderBlock({ onChanged });

    await openPanel('issuer-financials-toggle');
    fireEvent.change(await screen.findByTestId('issuer-financials-period'), { target: { value: '2024' } });
    fireEvent.change(screen.getByTestId('issuer-financials-revenue'), { target: { value: '12 500 000 000' } });
    fireEvent.change(screen.getByTestId('issuer-financials-interestPaid'), { target: { value: '300000000,50' } });
    fireEvent.click(screen.getByTestId('issuer-financials-save'));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(posted).toEqual({ period: 2024, revenue: 12_500_000_000, interestPaid: 300_000_000.5 });
  });

  it('rejects a non-numeric amount and an empty form locally, 422 from the server is shown inline', async () => {
    const user = userEvent.setup();
    server.use(
      http.post('*/api/issuers/:inn/financials', () =>
        HttpResponse.json({ error: 'revenue не может быть отрицательным', type: 'ValidationException' }, { status: 422 }),
      ),
    );
    renderBlock();

    await openPanel('issuer-financials-toggle');
    await user.click(await screen.findByTestId('issuer-financials-save'));
    expect(await screen.findByTestId('issuer-financials-form-error')).toHaveTextContent('хотя бы одну строку');

    fireEvent.change(screen.getByTestId('issuer-financials-revenue'), { target: { value: 'много' } });
    await user.click(screen.getByTestId('issuer-financials-save'));
    expect(await screen.findByTestId('issuer-financials-form-error')).toHaveTextContent('ожидается число');

    fireEvent.change(screen.getByTestId('issuer-financials-revenue'), { target: { value: '-5' } });
    await user.click(screen.getByTestId('issuer-financials-save'));
    await waitFor(() => expect(screen.getByTestId('issuer-financials-form-error')).toHaveTextContent('не может быть отрицательным'));
  });

  it('lists existing years with source and GIR BO status', async () => {
    server.use(
      http.get('*/api/issuers/:inn/financials', ({ params }) =>
        HttpResponse.json({
          inn: params.inn,
          girBoStatus: 'hidden',
          years: [
            { period: 2023, source: 'manual', revenue: 5_000_000_000, equity: 1_000_000_000 },
            { period: 2022, source: 'girbo', revenue: 4_000_000_000, equity: null },
          ],
          disclaimer: '',
        }),
      ),
    );
    renderBlock();

    await openPanel('issuer-financials-toggle');
    expect(await screen.findByTestId('issuer-financials-year-2023')).toHaveTextContent('вручную');
    expect(screen.getByTestId('issuer-financials-year-2022')).toHaveTextContent('ГИР БО');
    expect(screen.getByTestId('issuer-financials-girbo-status')).toHaveTextContent('эмитент скрыл отчётность');
  });
});

describe('IssuerDataBlock — ИНН', () => {
  it('asks for the INN when none is known and validates it before showing forms', async () => {
    const user = userEvent.setup();
    render(
      <MantineProvider>
        <IssuerDataBlock defaultInn={null} />
      </MantineProvider>,
    );

    expect(screen.queryByTestId('issuer-data-panel')).not.toBeInTheDocument();
    await user.type(screen.getByTestId('issuer-inn-input'), '123');
    await user.click(screen.getByTestId('issuer-inn-apply'));
    expect(await screen.findByText(/10 цифр/)).toBeInTheDocument();
    expect(screen.queryByTestId('issuer-data-panel')).not.toBeInTheDocument();

    await user.clear(screen.getByTestId('issuer-inn-input'));
    await user.type(screen.getByTestId('issuer-inn-input'), INN);
    await user.click(screen.getByTestId('issuer-inn-apply'));
    expect(await screen.findByTestId('issuer-data-panel')).toBeInTheDocument();
  });
});
