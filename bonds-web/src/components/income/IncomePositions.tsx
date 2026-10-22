import type { KeyboardEvent, ReactNode } from 'react';
import { Badge, Group, Paper, Stack, Table, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle, IconArrowDown, IconArrowUp, IconChevronDown } from '@tabler/icons-react';
import type { IncomeAccountLevel, IncomePeriod, IncomePosition, IncomeTotals } from '../../api/types';
import {
  costsOf,
  couponsOf,
  formatHolding,
  periodResultOf,
  positionKey,
  positionTitle,
  realizedOf,
  signClass,
  STATUS_LABEL,
  type IncomeSortKey,
  type IncomeSortState,
} from '../../utils/incomeDisplay';
import { formatDate, formatPercent, formatRub } from '../../utils/format';
import { INCOME_ACCRUED_UNKNOWN, INCOME_DATA_INCOMPLETE, INCOME_UNKNOWN_LOTS } from '../incomeCopy';
import { IncomeLedger } from './IncomeLedger';

function Money({ value, signed = false, fraction }: { value: number | null; signed?: boolean; fraction?: number | null }) {
  if (value === null) return <span className="bonds-note">—</span>;
  return (
    <span className={`mvv-num ${signClass(value)}`}>
      {signed && value > 0.005 ? '+' : ''}
      {formatRub(value)}
      {fraction !== undefined && fraction !== null && <div className="bonds-note">{formatPercent(fraction)}</div>}
    </span>
  );
}

/** Предупреждения по позиции: иконка + тултип (aria-label — для скринридеров и тестов). */
function Warnings({ p }: { p: IncomePosition }) {
  const items: Array<[string, string]> = [];
  if (p.dataIncomplete) items.push(['data', INCOME_DATA_INCOMPLETE]);
  if (p.hasUnknownLots) items.push(['lots', INCOME_UNKNOWN_LOTS]);
  if (p.accruedUnknown) items.push(['accrued', INCOME_ACCRUED_UNKNOWN]);
  if (items.length === 0) return null;
  return (
    <>
      {items.map(([k, text]) => (
        <Tooltip key={k} label={text} multiline w={260} withArrow events={{ hover: true, focus: true, touch: true }}>
          <span role="img" tabIndex={0} aria-label={text} className="bonds-warn-icon" data-testid={`income-warn-${k}`}>
            <IconAlertTriangle size={14} />
          </span>
        </Tooltip>
      ))}
    </>
  );
}

/** Купоны/выплаты строки; у валюты это проценты на остаток — если их нет (брокер не отдаёт), честный прочерк. */
function CouponsMoney({ p, periodMode }: { p: IncomePosition; periodMode: boolean }) {
  if (p.assetKind === 'currency' && Math.abs(couponsOf(p, periodMode)) < 0.005) return <span className="bonds-note">—</span>;
  return <Money value={couponsOf(p, periodMode)} />;
}

function AssetCell({ p }: { p: IncomePosition }) {
  const sub = [p.ticker, p.isin].filter(Boolean).join(' · ');
  return (
    <div className="bonds-income-asset">
      <Group gap={6} wrap="nowrap" align="center">
        <Text fw={600} size="sm" lineClamp={2}>
          {positionTitle(p)}
        </Text>
        <Warnings p={p} />
      </Group>
      <Group gap={4} wrap="wrap" mt={2}>
        {p.assetKind === 'fund' && (
          <Badge size="xs" color="violet" variant="light">
            Фонд
          </Badge>
        )}
        {p.assetKind === 'currency' && (
          <Badge size="xs" color="teal" variant="light" data-testid={`income-badge-currency-${p.assetId}`}>
            Валюта
          </Badge>
        )}
        {p.status !== 'open' && (
          <Badge size="xs" color="gray" variant="light">
            {STATUS_LABEL[p.status]}
          </Badge>
        )}
        {sub && <span className="bonds-note">{sub}</span>}
      </Group>
    </div>
  );
}

function rowKeyDown(toggle: () => void) {
  return (e: KeyboardEvent) => {
    if (e.target !== e.currentTarget) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      toggle();
    }
  };
}

function SortTh({ label, k, sort, onSort, right = true, hint }: { label: ReactNode; k: IncomeSortKey; sort: IncomeSortState; onSort: (k: IncomeSortKey) => void; right?: boolean; hint?: string }) {
  const active = sort.key === k;
  const ariaSort = active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none';
  const btn = (
    <UnstyledButton onClick={() => onSort(k)} className="bonds-sort-btn" data-testid={`income-sort-${k}`}>
      {label}
      {active && (sort.dir === 'asc' ? <IconArrowUp size={12} /> : <IconArrowDown size={12} />)}
    </UnstyledButton>
  );
  return (
    <Table.Th ta={right ? 'right' : 'left'} aria-sort={ariaSort}>
      {hint ? (
        <Tooltip label={hint} multiline w={240} withArrow>
          {btn}
        </Tooltip>
      ) : (
        btn
      )}
    </Table.Th>
  );
}

interface CommonProps {
  rows: IncomePosition[];
  periodMode: boolean;
  expanded: ReadonlySet<string>;
  focusKey: string | null;
  onToggle: (p: IncomePosition) => void;
}

export function IncomePositionsTable({
  rows,
  periodMode,
  expanded,
  focusKey,
  onToggle,
  sort,
  onSort,
  totals,
  accountLevel,
  period,
}: CommonProps & {
  sort: IncomeSortState;
  onSort: (k: IncomeSortKey) => void;
  totals: IncomeTotals;
  accountLevel: IncomeAccountLevel;
  period: IncomePeriod;
}) {
  const accountCosts = accountLevel.taxesRub + accountLevel.feesRub;
  const totalsCosts = totals.taxesRub + totals.feesRub + accountCosts;
  // Период: налоги/комиссии «по счёту» не принадлежат строкам, но входят в KPI (`data.period`) — футер сходится с KPI.
  const rowsPeriodCosts = rows.reduce((s, r) => s + r.periodTaxesRub + r.periodFeesRub, 0);
  const periodCosts = period.taxesRub + period.feesRub;
  const periodAccountCosts = periodCosts - rowsPeriodCosts;
  const colCount = periodMode ? 10 : 9;
  const suffix = periodMode ? ', за период' : '';
  return (
    <Table.ScrollContainer minWidth={periodMode ? 1100 : 980}>
      <Table highlightOnHover data-testid="income-table">
        <Table.Thead>
          <Table.Tr>
            <SortTh label="Актив" k="name" sort={sort} onSort={onSort} right={false} />
            <SortTh label="Владение" k="holding" sort={sort} onSort={onSort} />
            <SortTh label="Вложено" k="invested" sort={sort} onSort={onSort} hint="Сумма всех покупок (без НКД) за всё время." />
            <SortTh
              label={periodMode ? 'Купоны и выплаты, брутто' : 'Купоны и выплаты, нетто'}
              k="coupons"
              sort={sort}
              onSort={onSort}
              hint={periodMode ? 'Купоны, выплаты фондов и проценты на остаток валюты за период до налога.' : 'Брутто − НКД уплаченный + НКД полученный + накопленный НКД.'}
            />
            <SortTh label={`Продажи${suffix}`} k="realized" sort={sort} onSort={onSort} hint="Прибыль или убыток от продаж и погашений; у валюты — реализованная курсовая разница." />
            <SortTh label="Бумажная" k="unrealized" sort={sort} onSort={onSort} hint="На сегодня, за всё время. У закрытых позиций нет. У валюты — курсовая разница по текущему курсу." />
            <SortTh label={`Налоги и комиссии${suffix}`} k="costs" sort={sort} onSort={onSort} />
            {periodMode && <SortTh label="Итого за период" k="periodNet" sort={sort} onSort={onSort} hint="Купоны + продажи − налоги − комиссии за период. Без НКД и бумажной прибыли." />}
            <SortTh label="Итого, всё время" k="total" sort={sort} onSort={onSort} hint="Всё заработанное по бумаге за всё время владения, ₽ и % к вложенному." />
            <SortTh label="XIRR" k="xirr" sort={sort} onSort={onSort} hint="Годовая доходность с учётом времени вложений." />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((p) => {
            const key = positionKey(p);
            const open = expanded.has(key);
            return [
              <Table.Tr
                key={key}
                className="bonds-income-row"
                data-testid={`income-row-${key}`}
                data-focused={focusKey === key || undefined}
                tabIndex={0}
                aria-expanded={open}
                onClick={() => onToggle(p)}
                onKeyDown={rowKeyDown(() => onToggle(p))}
              >
                <Table.Td>
                  <Group gap={6} wrap="nowrap" align="flex-start">
                    <IconChevronDown size={14} className="bonds-disclosure-icon" data-open={open || undefined} aria-hidden="true" />
                    <AssetCell p={p} />
                  </Group>
                </Table.Td>
                <Table.Td ta="right">
                  <div className="mvv-num">{formatHolding(p.holdingDays)}</div>
                  <div className="bonds-note">
                    {p.status === 'open' ? `с ${formatDate(p.firstDate)}` : `${formatDate(p.firstDate)} — ${formatDate(p.lastDate)}`}
                  </div>
                </Table.Td>
                <Table.Td ta="right">
                  <Money value={p.investedTotalRub} />
                </Table.Td>
                <Table.Td ta="right">
                  <CouponsMoney p={p} periodMode={periodMode} />
                </Table.Td>
                <Table.Td ta="right">
                  <Money value={realizedOf(p, periodMode)} signed />
                </Table.Td>
                <Table.Td ta="right">
                  <Money value={p.unrealizedPnlRub} signed />
                </Table.Td>
                <Table.Td ta="right">
                  <Money value={costsOf(p, periodMode)} />
                </Table.Td>
                {periodMode && (
                  <Table.Td ta="right">
                    <Money value={periodResultOf(p)} signed />
                  </Table.Td>
                )}
                <Table.Td ta="right">
                  <Money value={p.totalResultRub} signed fraction={p.totalReturnFraction} />
                </Table.Td>
                <Table.Td ta="right" className="mvv-num">
                  {formatPercent(p.xirrFraction)}
                </Table.Td>
              </Table.Tr>,
              open && (
                <Table.Tr key={`${key}-ledger`} className="bonds-income-ledger-row">
                  <Table.Td colSpan={colCount}>
                    <IncomeLedger position={p} />
                  </Table.Td>
                </Table.Tr>
              ),
            ];
          })}
        </Table.Tbody>
        <Table.Tfoot>
          <Table.Tr data-testid="income-totals-row" className="bonds-income-totals">
            <Table.Td>Итого ({totals.positionsCount})</Table.Td>
            <Table.Td />
            <Table.Td ta="right">
              <Money value={totals.investedTotalRub} />
            </Table.Td>
            <Table.Td ta="right">
              <Money value={periodMode ? rows.reduce((s, r) => s + r.periodIncomeGrossRub, 0) : totals.netCouponIncomeRub} />
            </Table.Td>
            <Table.Td ta="right">
              <Money value={periodMode ? rows.reduce((s, r) => s + r.periodRealizedPnlRub, 0) : totals.realizedPnlRub} signed />
            </Table.Td>
            <Table.Td ta="right">
              <Money value={totals.unrealizedPnlRub} signed />
            </Table.Td>
            <Table.Td ta="right">
              <Money value={periodMode ? periodCosts : totalsCosts} />
            </Table.Td>
            {periodMode && (
              <Table.Td ta="right">
                <Money value={rows.reduce((s, r) => s + periodResultOf(r), 0) + periodAccountCosts} signed />
              </Table.Td>
            )}
            <Table.Td ta="right">
              <Money value={totals.totalResultRub} signed fraction={totals.totalReturnFraction} />
            </Table.Td>
            <Table.Td />
          </Table.Tr>
        </Table.Tfoot>
      </Table>
    </Table.ScrollContainer>
  );
}

/** Мобильная версия: карточка на позицию, тап — раскрывает журнал. Без вложенных кнопок (div[role=button]). */
export function IncomePositionCards({ rows, periodMode, expanded, focusKey, onToggle }: CommonProps) {
  return (
    <Stack gap="sm" p="sm" data-testid="income-cards">
      {rows.map((p) => {
        const key = positionKey(p);
        const open = expanded.has(key);
        return (
          <Paper key={key} withBorder p="sm" radius="md" data-testid={`income-card-${key}`}>
            <div
              role="button"
              tabIndex={0}
              aria-expanded={open}
              className="bonds-income-card-head"
              onClick={() => onToggle(p)}
              onKeyDown={rowKeyDown(() => onToggle(p))}
              data-testid={`income-row-${key}`}
              data-focused={focusKey === key || undefined}
            >
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <AssetCell p={p} />
                <div style={{ textAlign: 'right', flex: 'none' }}>
                  <div className="bonds-eyebrow">Итого</div>
                  <Money value={p.totalResultRub} signed fraction={p.totalReturnFraction} />
                </div>
              </Group>
              <div className="bonds-income-card-grid">
                <div>
                  <div className="bonds-eyebrow">Вложено</div>
                  <Money value={p.investedTotalRub} />
                </div>
                <div>
                  <div className="bonds-eyebrow">{periodMode ? 'Купоны, брутто' : 'Купоны, нетто'}</div>
                  <CouponsMoney p={p} periodMode={periodMode} />
                </div>
                <div>
                  <div className="bonds-eyebrow">Продажи</div>
                  <Money value={realizedOf(p, periodMode)} signed />
                </div>
                <div>
                  <div className="bonds-eyebrow">Бумажная</div>
                  <Money value={p.unrealizedPnlRub} signed />
                </div>
                <div>
                  <div className="bonds-eyebrow">Налоги и комиссии</div>
                  <Money value={costsOf(p, periodMode)} />
                </div>
                <div>
                  <div className="bonds-eyebrow">XIRR · владение</div>
                  <span className="mvv-num">
                    {formatPercent(p.xirrFraction)} · {formatHolding(p.holdingDays)}
                  </span>
                </div>
              </div>
            </div>
            {open && (
              <div className="bonds-mt">
                <IncomeLedger position={p} />
              </div>
            )}
          </Paper>
        );
      })}
    </Stack>
  );
}
