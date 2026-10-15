import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import userEvent from '@testing-library/user-event';
import { server } from '../test/msw-handlers';
import { Signals } from './Signals';
import { useSignalsStore } from '../store/useSignalsStore';
import type { Signal } from '../api/types';

function renderSignals() {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <Signals />
      </MemoryRouter>
    </MantineProvider>,
  );
}

// Фикстуры — с РЕАЛЬНЫМИ значениями enum бэкенда (SignalType/SignalSeverity: Info|Warning|Critical).
const putOffer: Signal = {
  id: 1,
  type: 'UpcomingOffer',
  severity: 'Critical',
  instrumentId: 10,
  subject: 'Сбербанк БО-П13',
  suggestedAction: 'Put-оферта: решите до этой даты, предъявлять ли бумагу к выкупу.',
  eventDate: '2099-03-10',
  firstSeenAt: '2099-03-01T08:00:00Z',
  isRead: false,
  isResolved: false,
  resolvedAt: null,
};

const concentration: Signal = {
  id: 2,
  type: 'ConcentrationLimitBreached',
  severity: 'Warning',
  instrumentId: null,
  subject: 'Минфин РФ',
  suggestedAction: 'Доля эмитента 40% при лимите 25% — рассмотрите диверсификацию (оценка).',
  eventDate: null,
  firstSeenAt: '2099-03-01T08:00:00Z',
  isRead: true,
  isResolved: false,
  resolvedAt: null,
};

const redemption: Signal = {
  id: 3,
  type: 'UpcomingRedemption',
  severity: 'Info',
  instrumentId: 11,
  subject: 'ОФЗ 26238',
  suggestedAction: 'Погашение: кандидат на реинвестирование освободившихся денег.',
  eventDate: '2099-03-05',
  firstSeenAt: '2099-03-01T08:00:00Z',
  isRead: false,
  isResolved: false,
  resolvedAt: null,
};

const earlierPut: Signal = { ...putOffer, id: 4, subject: 'Газпром БО-12', eventDate: '2099-03-02' };

const closed: Signal = {
  ...redemption,
  id: 5,
  subject: 'Старая бумага',
  isRead: true,
  isResolved: true,
  resolvedAt: '2099-03-02T08:00:00Z',
};

describe('Signals («События»)', () => {
  beforeEach(() => {
    useSignalsStore.setState({
      signals: [],
      history: [],
      historyLoaded: false,
      isLoading: false,
      error: null,
    });
  });

  it('groups by severity (Critical first), shows bond name and event date, no raw enum names', async () => {
    server.use(
      http.get('*/api/signals', () => HttpResponse.json({ signals: [redemption, concentration, putOffer] })),
    );

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signal-1')).toBeInTheDocument());

    const groups = screen.getAllByTestId(/^signals-group-/).map((g) => g.dataset.testid);
    expect(groups).toEqual(['signals-group-Critical', 'signals-group-Warning', 'signals-group-Info']);

    // Имя бумаги вместо «позиция #id»; дата события; русские метки.
    expect(screen.getByTestId('signal-subject-1')).toHaveTextContent('Сбербанк БО-П13');
    expect(screen.getByTestId('signal-1')).toHaveTextContent('Дата события:');
    expect(screen.getByTestId('signal-1')).toHaveTextContent('Требует решения');
    expect(screen.getByTestId('signal-1')).toHaveTextContent('Оферта');
    expect(screen.getByTestId('signal-2')).toHaveTextContent('Текущее состояние');
    expect(screen.getByTestId('signal-2')).toHaveTextContent('Концентрация по эмитенту');

    const list = screen.getByTestId('signals-list');
    for (const raw of ['UpcomingOffer', 'Critical', 'Warning', 'Info', 'ConcentrationLimitBreached', 'позиция #']) {
      expect(list).not.toHaveTextContent(raw);
    }
  });

  it('orders events within a group by event date (earliest first)', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ signals: [putOffer, earlierPut] })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signals-group-Critical')).toBeInTheDocument());
    const cards = within(screen.getByTestId('signals-group-Critical')).getAllByTestId(/^signal-\d+$/);
    expect(cards.map((c) => c.dataset.testid)).toEqual(['signal-4', 'signal-1']);
  });

  it('marks a single event read: badge and button disappear, event stays listed', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ signals: [putOffer] })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('mark-read-1')).toBeInTheDocument());
    expect(screen.getByText('новое')).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('mark-read-1'));

    await waitFor(() => expect(screen.queryByTestId('mark-read-1')).not.toBeInTheDocument());
    expect(screen.queryByText('новое')).not.toBeInTheDocument();
    expect(screen.getByTestId('signal-1')).toBeInTheDocument();
  });

  it('marks all read via the header button, which then disappears', async () => {
    let called = false;
    server.use(
      http.get('*/api/signals', () => HttpResponse.json({ signals: [putOffer, redemption] })),
      http.post('*/api/signals/read-all', () => {
        called = true;
        return HttpResponse.json({ acknowledged: 2 });
      }),
    );

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('mark-all-read')).toBeInTheDocument());
    await userEvent.click(screen.getByTestId('mark-all-read'));

    await waitFor(() => expect(screen.queryByTestId('mark-all-read')).not.toBeInTheDocument());
    expect(called).toBe(true);
    expect(screen.queryByTestId('mark-read-1')).not.toBeInTheDocument();
    expect(screen.queryByTestId('mark-read-3')).not.toBeInTheDocument();
  });

  it('shows the "nothing requires attention" empty state', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ signals: [] })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signals-empty')).toBeInTheDocument());
    expect(screen.getByText('Ничего не требует внимания')).toBeInTheDocument();
  });

  it('loads resolved events only when the collapsed history is opened', async () => {
    const requests: string[] = [];
    server.use(
      http.get('*/api/signals', ({ request }) => {
        requests.push(new URL(request.url).search);
        const resolved = new URL(request.url).searchParams.get('include') === 'resolved';
        return HttpResponse.json({ signals: resolved ? [putOffer, closed] : [putOffer] });
      }),
    );

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signal-1')).toBeInTheDocument());
    expect(requests).toEqual(['']);

    await userEvent.click(screen.getByTestId('history-toggle'));

    await waitFor(() => expect(screen.getByTestId('signal-5')).toBeVisible());
    expect(screen.getByTestId('signal-5')).toHaveTextContent('Старая бумага');
    expect(requests).toContain('?include=resolved');
    // Активное событие не дублируется в истории.
    expect(screen.getAllByTestId('signal-1')).toHaveLength(1);
  });

  it('shows an error state without crashing when the request fails', async () => {
    server.use(http.get('*/api/signals', () => HttpResponse.json({ error: 'boom' }, { status: 500 })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signals-error')).toBeInTheDocument());
  });

  // ─── Задача 49: рыночные и кредитные события ───────────────────────────────────────────

  it('shows new market and credit event types with labels and icons, grouped by severity', async () => {
    const spread: Signal = {
      ...concentration,
      id: 21,
      type: 'SpreadWidening',
      severity: 'Warning',
      instrumentId: 10,
      subject: 'Бумага А',
      suggestedAction: 'G-спред бумаги расширился на 60 б.п. к медиане за 20 торговых дн.',
      isRead: false,
    };
    const drawdown: Signal = { ...spread, id: 22, type: 'PriceDrawdown', subject: 'Бумага Б', suggestedAction: 'Цена на 6.0% ниже максимума.' };
    const issuerDefault: Signal = {
      ...putOffer,
      id: 23,
      type: 'IssuerDefaultEvent',
      severity: 'Critical',
      subject: 'Эмитент В',
      eventDate: null,
      suggestedAction: 'У эмитента отмечен дефолт.',
    };
    server.use(http.get('*/api/signals', () => HttpResponse.json({ signals: [spread, drawdown, issuerDefault] })));

    renderSignals();

    await waitFor(() => expect(screen.getByTestId('signal-23')).toBeInTheDocument());
    expect(within(screen.getByTestId('signals-group-Critical')).getByTestId('signal-23')).toHaveTextContent('Дефолт эмитента');
    const warnings = screen.getByTestId('signals-group-Warning');
    expect(within(warnings).getByTestId('signal-21')).toHaveTextContent('Расширение G-спреда');
    expect(within(warnings).getByTestId('signal-22')).toHaveTextContent('Просадка цены');
    expect(screen.getByTestId('signal-type-21').querySelector('svg')).not.toBeNull();
    expect(screen.getByTestId('signal-type-23').querySelector('svg')).not.toBeNull();
    // сырые имена enum пользователю не показываются
    const list = screen.getByTestId('signals-list');
    for (const raw of ['SpreadWidening', 'PriceDrawdown', 'IssuerDefaultEvent']) {
      expect(list).not.toHaveTextContent(raw);
    }
  });
});
