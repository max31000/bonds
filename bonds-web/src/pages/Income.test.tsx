import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '../test/msw-handlers';
import { baseIncome, bondLedger, closedBond, fundLedger } from '../test/incomeFixtures';
import { useIncomeStore } from '../store/useIncomeStore';
import { Income } from './Income';

function Loc() {
  const l = useLocation();
  return <div data-testid="loc">{l.pathname + l.search}</div>;
}

function renderIncome(url = '/income') {
  return render(
    <MantineProvider>
      <MemoryRouter initialEntries={[url]}>
        <Loc />
        <Routes>
          <Route path="income" element={<Income />} />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Запоминает URL запросов /api/income и обращения за журналами. */
function mockIncome(opts: { income?: unknown } = {}) {
  const incomeUrls: URL[] = [];
  const ledgerCalls: string[] = [];
  server.use(
    http.get('*/api/income', ({ request }) => {
      incomeUrls.push(new URL(request.url));
      return HttpResponse.json(opts.income ?? baseIncome);
    }),
    http.get('*/api/income/:kind/:assetId/ledger', ({ params }) => {
      ledgerCalls.push(`${params.kind}:${params.assetId}`);
      return HttpResponse.json(params.kind === 'fund' ? fundLedger : bondLedger);
    }),
  );
  return { incomeUrls, ledgerCalls };
}

describe('Income (задача 54)', () => {
  beforeEach(() => {
    useIncomeStore.setState({ data: null, isLoading: false, error: null, firstYear: null, ledgers: {} });
  });

  it('показывает KPI, итоги и позиции: открытую и закрытую облигацию и фонд', async () => {
    mockIncome();
    renderIncome();

    const kpi = await screen.findByTestId('income-kpi');
    expect(within(kpi).getByTestId('income-kpi-total')).toHaveTextContent(/14\s956\s₽/);
    expect(within(kpi).getByTestId('income-kpi-total')).toHaveTextContent('9.37%');
    expect(within(kpi).getByTestId('income-kpi-coupons')).toHaveTextContent(/18\s360\s₽/);
    expect(within(kpi).getByTestId('income-kpi-realized')).toHaveTextContent(/1\s500\s₽/);
    expect(within(kpi).getByTestId('income-kpi-xirr')).toHaveTextContent('10.45%');
    // налоги «по счёту» включены в KPI: −2558.8 + (−10)
    expect(within(kpi).getByTestId('income-kpi-taxes')).toHaveTextContent(/2\s569\s₽/);

    expect(screen.getByTestId('income-row-bond:1')).toHaveTextContent('ОФЗ 26238');
    expect(screen.getByTestId('income-row-bond:2')).toHaveTextContent('Закрыта');
    expect(screen.getByTestId('income-row-fund:7')).toHaveTextContent('Фонд');
    expect(screen.getByTestId('income-totals-row')).toHaveTextContent(/14\s956\s₽/);
    expect(screen.getByTestId('income-breakdown-total')).toHaveTextContent(/14\s956\s₽/);
    expect(screen.getByTestId('income-monthly-avg')).toHaveTextContent('в месяц');
    expect(screen.getByTestId('disclaimer')).toHaveTextContent('Не инвестиционная рекомендация');
  });

  it('по умолчанию сортирует по итогу по убыванию, клик по заголовку меняет порядок', async () => {
    mockIncome();
    renderIncome();
    await screen.findByTestId('income-table');
    const ids = () => screen.getAllByTestId(/^income-row-/).map((r) => r.getAttribute('data-testid'));
    expect(ids()).toEqual(['income-row-bond:1', 'income-row-bond:2', 'income-row-fund:7']);
    await userEvent.click(screen.getByTestId('income-sort-invested')); // по убыванию
    await userEvent.click(screen.getByTestId('income-sort-invested')); // по возрастанию
    expect(ids()).toEqual(['income-row-fund:7', 'income-row-bond:2', 'income-row-bond:1']);
  });

  it('смена периода, типа актива и «закрытых» меняет query запроса и URL', async () => {
    const { incomeUrls } = mockIncome();
    renderIncome();
    await screen.findByTestId('income-kpi');
    expect(incomeUrls[0].searchParams.get('assetKind')).toBe('all');
    expect(incomeUrls[0].searchParams.get('includeClosed')).toBe('true');
    expect(incomeUrls[0].searchParams.has('from')).toBe(false);

    await userEvent.click(screen.getByLabelText('С начала года'));
    await waitFor(() => expect(screen.getByTestId('loc')).toHaveTextContent('period=ytd'));
    await waitFor(() =>
      expect(incomeUrls.some((u) => u.searchParams.get('from') === `${new Date().getFullYear()}-01-01`)).toBe(true),
    );

    await userEvent.click(screen.getByLabelText('Облигации'));
    await waitFor(() => expect(screen.getByTestId('loc')).toHaveTextContent('asset=bond'));
    await waitFor(() => expect(incomeUrls.some((u) => u.searchParams.get('assetKind') === 'bond')).toBe(true));

    await userEvent.click(screen.getByLabelText('Закрытые позиции'));
    await waitFor(() => expect(screen.getByTestId('loc')).toHaveTextContent('closed=0'));
    await waitFor(() => expect(incomeUrls.some((u) => u.searchParams.get('includeClosed') === 'false')).toBe(true));
  });

  it('в режиме периода KPI «за период» и пометка, что бумажная прибыль не зависит от периода', async () => {
    mockIncome();
    renderIncome('/income?period=12m');
    const kpi = await screen.findByTestId('income-kpi');
    expect(within(kpi).getByTestId('income-kpi-period')).toHaveTextContent(/4\s424\s₽/);
    expect(within(kpi).getByTestId('income-kpi-total')).toHaveTextContent('Заработано за всё время');
    expect(within(kpi).getByTestId('income-kpi-unrealized')).toHaveTextContent('период не влияет');
    expect(screen.getByTestId('income-positions')).toHaveTextContent('за период');
  });

  it('бумажная прибыль помечена «за всё время» и без периода', async () => {
    mockIncome();
    renderIncome();
    expect(await screen.findByTestId('income-kpi-unrealized')).toHaveTextContent('на сегодня, за всё время');
  });

  it('раскрытие строки грузит журнал один раз (кэш) и показывает человеческие названия операций', async () => {
    const { ledgerCalls } = mockIncome();
    renderIncome();
    const row = await screen.findByTestId('income-row-bond:1');
    expect(row).toHaveAttribute('aria-expanded', 'false');

    await userEvent.click(row);
    const ledger = await screen.findByTestId('income-ledger-bond-1');
    await within(ledger).findByText('Покупка');
    expect(ledger).toHaveTextContent('Купон');
    expect(ledger).toHaveTextContent('Налог');
    expect(ledger).toHaveTextContent('Амортизация');
    expect(row).toHaveAttribute('aria-expanded', 'true');
    expect(within(ledger).getByText('Открыть карточку').closest('a')).toHaveAttribute('href', '/positions/11');

    await userEvent.click(row); // свернуть
    await waitFor(() => expect(screen.queryByTestId('income-ledger-bond-1')).not.toBeInTheDocument());
    await userEvent.click(row); // развернуть снова
    await screen.findByTestId('income-ledger-bond-1');
    expect(ledgerCalls).toEqual(['bond:1']);
  });

  it('раскрытие с клавиатуры (Enter) и «Выплата фонда» у фонда со ссылкой на карточку фонда', async () => {
    mockIncome();
    renderIncome();
    const row = await screen.findByTestId('income-row-fund:7');
    row.focus();
    await userEvent.keyboard('{Enter}');
    const ledger = await screen.findByTestId('income-ledger-fund-7');
    await within(ledger).findByText('Выплата фонда');
    expect(within(ledger).getByText('Открыть карточку').closest('a')).toHaveAttribute('href', '/funds/7');
  });

  it('у закрытой позиции нет ссылки на карточку', async () => {
    mockIncome();
    renderIncome();
    await userEvent.click(await screen.findByTestId('income-row-bond:2'));
    const ledger = await screen.findByTestId('income-ledger-bond-2');
    await within(ledger).findByText('Покупка');
    expect(within(ledger).queryByText('Открыть карточку')).not.toBeInTheDocument();
  });

  it('hasUnknownLots даёт предупреждение; у чистой позиции его нет', async () => {
    mockIncome();
    renderIncome();
    const closed = await screen.findByTestId('income-row-bond:2');
    expect(within(closed).getByTestId('income-warn-lots')).toHaveAccessibleName(/История операций по этой бумаге неполная/);
    expect(within(screen.getByTestId('income-row-bond:1')).queryByTestId('income-warn-lots')).not.toBeInTheDocument();
  });

  it('dataIncomplete и accruedUnknown дают свои предупреждения', async () => {
    mockIncome({
      income: { ...baseIncome, positions: [{ ...closedBond, hasUnknownLots: false, dataIncomplete: true, accruedUnknown: true }] },
    });
    renderIncome();
    const row = await screen.findByTestId('income-row-bond:2');
    expect(within(row).getByTestId('income-warn-data')).toBeInTheDocument();
    expect(within(row).getByTestId('income-warn-accrued')).toBeInTheDocument();
  });

  it('?asset=bond&focus=<assetId> раскрывает нужную строку и грузит её журнал', async () => {
    const { ledgerCalls } = mockIncome();
    renderIncome('/income?asset=bond&focus=2');
    const row = await screen.findByTestId('income-row-bond:2');
    await waitFor(() => expect(row).toHaveAttribute('aria-expanded', 'true'));
    await screen.findByTestId('income-ledger-bond-2');
    expect(ledgerCalls).toEqual(['bond:2']);
  });

  it('пустой журнал — понятное пустое состояние', async () => {
    renderIncome();
    expect(await screen.findByTestId('income-empty')).toHaveTextContent('появятся после синхронизации с брокером');
    expect(screen.getByTestId('disclaimer')).toBeInTheDocument();
  });

  it('ошибка сводки показывается с повтором; повтор перезапрашивает данные', async () => {
    let fail = true;
    server.use(
      http.get('*/api/income', () => (fail ? HttpResponse.json({ error: 'boom' }, { status: 500 }) : HttpResponse.json(baseIncome))),
    );
    renderIncome();
    const err = await screen.findByTestId('income-error');
    fail = false;
    await userEvent.click(within(err).getByRole('button'));
    expect(await screen.findByTestId('income-kpi')).toBeInTheDocument();
  });

  it('ошибка журнала не роняет сводку и даёт повтор', async () => {
    let fail = true;
    server.use(
      http.get('*/api/income', () => HttpResponse.json(baseIncome)),
      http.get('*/api/income/:kind/:assetId/ledger', () =>
        fail ? HttpResponse.json({ error: 'boom' }, { status: 500 }) : HttpResponse.json(bondLedger),
      ),
    );
    renderIncome();
    await userEvent.click(await screen.findByTestId('income-row-bond:1'));
    const err = await screen.findByTestId('income-ledger-bond-1-error');
    expect(screen.getByTestId('income-kpi')).toBeInTheDocument();
    fail = false;
    await userEvent.click(within(err).getByRole('button'));
    await within(await screen.findByTestId('income-ledger-bond-1')).findByText('Покупка');
  });
});
