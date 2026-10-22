import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Group, Loader, SegmentedControl, Select, Skeleton, Switch } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { EmptyState, ErrorState, HelpHint, Metric, MetricGrid, Page, Section } from '@mvv42/kit';
import { useSearchParams } from 'react-router-dom';
import type { IncomePosition, IncomeResponse } from '../api/types';
import { Disclaimer } from '../components/Disclaimer';
import { INCOME_CURRENCY_NOTE, INCOME_HELP } from '../components/incomeCopy';
import { IncomeBreakdown } from '../components/income/IncomeBreakdown';
import { IncomeMonthlyChart } from '../components/income/IncomeMonthlyChart';
import { IncomePositionCards, IncomePositionsTable } from '../components/income/IncomePositions';
import { useIncomeStore } from '../store/useIncomeStore';
import {
  DEFAULT_INCOME_SORT,
  formatSignedRub,
  parseAssetFilter,
  parsePeriod,
  parseShowClosed,
  periodNetCoupons,
  periodRange,
  positionKey,
  signClass,
  sortPositions,
  type IncomeSortKey,
  type IncomeSortState,
} from '../utils/incomeDisplay';
import { formatPercent, formatRub } from '../utils/format';

function KpiMetric({
  label,
  help,
  value,
  tone,
  hint,
  testId,
}: {
  label: string;
  help: string;
  value: string;
  tone?: number | null;
  hint?: ReactNode;
  testId: string;
}) {
  return (
    <Metric
      data-testid={testId}
      label={
        <Group gap={4} wrap="nowrap" component="span">
          {label}
          <HelpHint label={`Пояснение: ${label}`}>{help}</HelpHint>
        </Group>
      }
      value={<span className={signClass(tone)}>{value}</span>}
      hint={hint}
    />
  );
}

/**
 * KPI-блок. В режиме периода денежные KPI (купоны, продажи, налоги, комиссии) — за период, «бумажная прибыль»
 * и «заработано за всё время» — всегда all-time (бэкенд не режет их периодом, `unrealizedIsAllTime`).
 * «Чистые купоны» all-time = `totals.netCouponIncomeRub` (брутто − НКД уплаченный + НКД полученный + набежавший НКД, БЕЗ налогов —
 * налоги выведены в отдельный KPI).
 */
function KpiBlock({ data, periodMode }: { data: IncomeResponse; periodMode: boolean }) {
  const { totals: t, accountLevel: acc, period: p } = data;
  const costsTaxes = periodMode ? p.taxesRub : t.taxesRub + acc.taxesRub;
  const costsFees = periodMode ? p.feesRub : t.feesRub + acc.feesRub;
  const coupons = periodMode ? periodNetCoupons(p) : t.netCouponIncomeRub;
  const realized = periodMode ? p.realizedPnlRub : t.realizedPnlRub;
  const grossHint = periodMode
    ? `брутто ${formatRub(p.couponsGrossRub + p.distributionsGrossRub)} · НКД уплачен ${formatRub(-Math.abs(p.accruedPaidRub))} · получен ${formatRub(Math.abs(p.accruedReceivedRub))}`
    : `брутто ${formatRub(t.incomeGrossRub)} · НКД уплачен ${formatRub(-Math.abs(t.accruedPaidRub))} · получен ${formatRub(Math.abs(t.accruedReceivedRub))} · набежал ${formatRub(t.accruedNowRub)}`;

  return (
    <MetricGrid data-testid="income-kpi">
      {periodMode ? (
        <>
          <KpiMetric
            testId="income-kpi-period"
            label="Заработано за период"
            help={INCOME_HELP.periodTotal}
            value={formatSignedRub(p.netRub, formatRub)}
            tone={p.netRub}
            hint="деньги периода, без бумажной прибыли"
          />
          <KpiMetric
            testId="income-kpi-total"
            label="Заработано за всё время"
            help={INCOME_HELP.total}
            value={formatSignedRub(t.totalResultRub, formatRub)}
            tone={t.totalResultRub}
            hint={t.totalReturnFraction !== null ? `${formatPercent(t.totalReturnFraction)} к вложенному` : undefined}
          />
        </>
      ) : (
        <KpiMetric
          testId="income-kpi-total"
          label="Заработано всего"
          help={INCOME_HELP.total}
          value={formatSignedRub(t.totalResultRub, formatRub)}
          tone={t.totalResultRub}
          hint={
            t.totalReturnFraction !== null
              ? `${formatPercent(t.totalReturnFraction)} к вложенному ${formatRub(t.investedTotalRub)}`
              : `вложено ${formatRub(t.investedTotalRub)}`
          }
        />
      )}
      <KpiMetric
        testId="income-kpi-coupons"
        label="Купоны и выплаты (чистые)"
        help={periodMode ? INCOME_HELP.couponsPeriod : INCOME_HELP.coupons}
        value={formatSignedRub(coupons, formatRub)}
        tone={coupons}
        hint={grossHint}
      />
      <KpiMetric
        testId="income-kpi-realized"
        label="Прибыль от продаж и погашений"
        help={INCOME_HELP.realized}
        value={formatSignedRub(realized, formatRub)}
        tone={realized}
        hint={periodMode ? 'за период' : 'за всё время'}
      />
      <KpiMetric
        testId="income-kpi-unrealized"
        label="Бумажная прибыль"
        help={INCOME_HELP.unrealized}
        value={formatSignedRub(t.unrealizedPnlRub, formatRub)}
        tone={t.unrealizedPnlRub}
        hint={
          data.unrealizedIsAllTime
            ? periodMode
              ? 'на сегодня, за всё время — период не влияет'
              : 'на сегодня, за всё время'
            : undefined
        }
      />
      <KpiMetric
        testId="income-kpi-taxes"
        label="Налоги"
        help={INCOME_HELP.taxes}
        value={formatRub(costsTaxes)}
        hint={periodMode ? 'за период' : acc.taxesRub !== 0 ? `в т.ч. «по счёту» ${formatRub(acc.taxesRub)}` : 'за всё время'}
      />
      <KpiMetric
        testId="income-kpi-fees"
        label="Комиссии"
        help={INCOME_HELP.fees}
        value={formatRub(costsFees)}
        hint={periodMode ? 'за период' : acc.feesRub !== 0 ? `в т.ч. «по счёту» ${formatRub(acc.feesRub)}` : 'за всё время'}
      />
      <KpiMetric
        testId="income-kpi-xirr"
        label="XIRR портфеля"
        help={INCOME_HELP.xirr}
        value={formatPercent(data.portfolioXirrFraction)}
        tone={data.portfolioXirrFraction}
        hint="годовых, облигации, фонды и валюта"
      />
    </MetricGrid>
  );
}

function PageSkeleton() {
  return (
    <div data-testid="income-skeleton">
      <Skeleton height={96} radius="md" mb="md" />
      <Skeleton height={280} radius="md" mb="md" />
      <Skeleton height={200} radius="md" />
    </div>
  );
}

/**
 * Экран «Доходы» (задача 54): сколько реально заработано по всем бумагам — облигациям и фондам, открытым и закрытым.
 * Фильтры живут в URL (`?period=all|ytd|12m|YYYY&asset=all|bond|fund|currency&closed=0|1`), `?asset=bond&focus=<assetId>` (для валюты — `?asset=currency&focus=<currencyId>`)
 * раскрывает и подсвечивает нужную строку. Все расчёты — на бэкенде (задача 52); здесь только показ.
 */
export function Income() {
  const [params, setParams] = useSearchParams();
  const period = parsePeriod(params.get('period'));
  const asset = parseAssetFilter(params.get('asset'));
  const showClosed = parseShowClosed(params.get('closed'));
  const focusRaw = params.get('focus');
  const periodMode = period !== 'all';
  const isYear = /^\d{4}$/.test(period);
  const isMobile = useMediaQuery('(max-width: 48em)');

  const { data, isLoading, error, firstYear, load, loadLedger } = useIncomeStore();
  const [sort, setSort] = useState<IncomeSortState>(DEFAULT_INCOME_SORT);

  const reload = useCallback(() => {
    const { from, to } = periodRange(period);
    void load({ from, to, assetKind: asset, includeClosed: showClosed });
  }, [load, period, asset, showClosed]);

  useEffect(() => {
    reload();
  }, [reload]);

  const setParam = useCallback(
    (key: string, value: string | null) => {
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (value === null) next.delete(key);
          else next.set(key, value);
          next.delete('focus'); // фокус относится к ссылке-входу, после смены фильтров он не нужен
          return next;
        },
        { replace: true },
      );
    },
    [setParams],
  );

  // Ручные переключения строк; без переключения строка открыта, только если она в фокусе (?focus=).
  const [overrides, setOverrides] = useState<ReadonlyMap<string, boolean>>(new Map());

  // ?focus=<assetId> (+ ?asset=bond|fund): раскрыть строку и проскроллить к ней.
  const focusKey = useMemo(() => {
    if (!focusRaw || !data || asset === 'all') return null;
    const found = data.positions.find((p) => p.assetKind === asset && String(p.assetId) === focusRaw);
    return found ? positionKey(found) : null;
  }, [focusRaw, data, asset]);

  const expanded = useMemo(() => {
    const set = new Set<string>();
    if (focusKey && overrides.get(focusKey) !== false) set.add(focusKey);
    for (const [k, v] of overrides) if (v) set.add(k);
    return set;
  }, [focusKey, overrides]);

  const toggle = useCallback(
    (p: IncomePosition) => {
      const key = positionKey(p);
      setOverrides((prev) => new Map(prev).set(key, !expanded.has(key)));
      void loadLedger(p.assetKind, p.assetId);
    },
    [loadLedger, expanded],
  );

  const handledFocus = useRef<string | null>(null);
  useEffect(() => {
    if (!focusKey || !data || handledFocus.current === focusKey) return;
    const p = data.positions.find((x) => positionKey(x) === focusKey);
    if (!p) return;
    handledFocus.current = focusKey;
    void loadLedger(p.assetKind, p.assetId);
    requestAnimationFrame(() =>
      document.querySelector(`[data-testid="income-row-${focusKey}"]`)?.scrollIntoView?.({ block: 'center' }),
    );
  }, [focusKey, data, loadLedger]);

  const onSort = (key: IncomeSortKey) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === 'desc' ? 'asc' : 'desc' } : { key, dir: key === 'name' ? 'asc' : 'desc' }));

  const rows = useMemo(() => (data ? sortPositions(data.positions, sort, periodMode) : []), [data, sort, periodMode]);

  const currentYear = new Date().getFullYear();
  const yearOptions = useMemo(() => {
    const from = Math.min(firstYear ?? currentYear, isYear ? Number(period) : currentYear);
    const out: string[] = [];
    for (let y = currentYear; y >= from; y--) out.push(String(y));
    return out;
  }, [firstYear, currentYear, isYear, period]);

  const filters = (
    <Group gap="md" wrap="wrap" align="center" data-testid="income-filters">
      <SegmentedControl
        size="xs"
        value={isYear ? '' : period}
        onChange={(v) => setParam('period', v === 'all' ? null : v)}
        data={[
          { value: 'all', label: 'Всё время' },
          { value: 'ytd', label: 'С начала года' },
          { value: '12m', label: '12 мес.' },
        ]}
        aria-label="Период"
      />
      <Select
        size="xs"
        w={110}
        placeholder="Год"
        aria-label="Год"
        data={yearOptions}
        value={isYear ? period : null}
        onChange={(v) => setParam('period', v)}
        clearable
      />
      <SegmentedControl
        size="xs"
        value={asset}
        onChange={(v) => setParam('asset', v === 'all' ? null : v)}
        data={[
          { value: 'all', label: 'Все' },
          { value: 'bond', label: 'Облигации' },
          { value: 'fund', label: 'Фонды' },
          { value: 'currency', label: 'Валюта' },
        ]}
        aria-label="Тип актива"
      />
      <Switch
        size="sm"
        label="Закрытые позиции"
        checked={showClosed}
        onChange={(e) => setParam('closed', e.currentTarget.checked ? null : '0')}
      />
    </Group>
  );

  const hasOperations = !!data && (data.positions.length > 0 || data.monthly.some((m) => m.netRub !== 0));

  return (
    <Page
      title="Доходы"
      description="Сколько вы реально заработали: купоны, выплаты, продажи, налоги и комиссии"
      width="wide"
      data-testid="income-page"
    >
      <Section padding="sm">{filters}</Section>

      {isLoading && !data && <PageSkeleton />}

      {error && !isLoading && (
        <Section>
          <ErrorState title="Не удалось загрузить доходы" error={error} onRetry={reload} data-testid="income-error" />
        </Section>
      )}

      {!error && data && (
        <div className="bonds-income-body" data-loading={isLoading || undefined}>
          {!hasOperations ? (
            <Section>
              <EmptyState
                title="Операций пока нет"
                description="Операций пока нет — они появятся после синхронизации с брокером."
                data-testid="income-empty"
              />
            </Section>
          ) : (
            <>
              <KpiBlock data={data} periodMode={periodMode} />
              <IncomeMonthlyChart monthly={data.monthly} />
              <IncomeBreakdown totals={data.totals} accountLevel={data.accountLevel} />

              <Section
                padding="none"
                title={
                  <span className="bonds-inline-help">
                    Позиции
                    <HelpHint label="Пояснение: Позиции">
                      Нажмите на строку, чтобы увидеть все операции по бумаге. {INCOME_HELP.periodVsAll} {INCOME_HELP.accrued}
                    </HelpHint>
                  </span>
                }
                description={
                  periodMode
                    ? 'Колонки с пометкой «за период» — выбранный период; «Итого, всё время», «Бумажная», «Вложено» и XIRR — за всё время владения.'
                    : undefined
                }
                data-testid="income-positions"
              >
                {rows.length === 0 ? (
                  <EmptyState compact title="Нет позиций в этом срезе" description="Смените тип актива или включите закрытые позиции." />
                ) : isMobile ? (
                  <IncomePositionCards rows={rows} periodMode={periodMode} expanded={expanded} focusKey={focusKey} onToggle={toggle} />
                ) : (
                  <IncomePositionsTable
                    rows={rows}
                    periodMode={periodMode}
                    expanded={expanded}
                    focusKey={focusKey}
                    onToggle={toggle}
                    sort={sort}
                    onSort={onSort}
                    totals={data.totals}
                    accountLevel={data.accountLevel}
                    period={data.period}
                  />
                )}
                {data.positions.some((p) => p.assetKind === 'currency') && (
                  <p className="bonds-note bonds-income-footnote" data-testid="income-currency-note">
                    {INCOME_CURRENCY_NOTE}
                  </p>
                )}
                {(data.accountLevel.taxesRub !== 0 || data.accountLevel.feesRub !== 0) && (
                  <p className="bonds-note bonds-income-footnote" data-testid="income-account-level-note">
                    В итоге учтены налоги {formatRub(data.accountLevel.taxesRub)} и комиссии {formatRub(data.accountLevel.feesRub)} «по
                    счёту» — они не относятся к конкретной бумаге, поэтому в строках позиций их нет.
                  </p>
                )}
              </Section>
            </>
          )}
        </div>
      )}

      {isLoading && data && (
        <div className="bonds-center">
          <Loader size="sm" />
        </div>
      )}

      <Disclaimer text={data?.disclaimer} />
    </Page>
  );
}
