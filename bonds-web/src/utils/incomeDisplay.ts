import type {
  IncomeAccountLevel,
  IncomeAssetFilter,
  IncomeLedgerKind,
  IncomePosition,
  IncomePeriod,
  IncomeStatus,
  IncomeTotals,
} from '../api/types';

/** Период страницы: all | ytd | 12m | четырёхзначный год. Живёт в URL (`?period=`). */
export type IncomePeriodKey = string;

export function parsePeriod(raw: string | null): IncomePeriodKey {
  if (raw === 'ytd' || raw === '12m') return raw;
  if (raw && /^\d{4}$/.test(raw)) return raw;
  return 'all';
}

export function parseAssetFilter(raw: string | null): IncomeAssetFilter {
  return raw === 'bond' || raw === 'fund' ? raw : 'all';
}

/**
 * Закрытые позиции показываются по умолчанию (иначе итог «заработано» теряет результат проданного и погашенного);
 * `?closed=0` их скрывает, `?closed=1` — явное «показать» (совместимо со ссылками из плана).
 */
export function parseShowClosed(raw: string | null): boolean {
  return raw !== '0';
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

/** Локальная календарная дата `yyyy-MM-dd` (без UTC-сдвига `toISOString`). */
export function toIsoDate(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Границы периода (включительно) для `from`/`to`; «всё время» — без границ. */
export function periodRange(period: IncomePeriodKey, today: Date = new Date()): { from?: string; to?: string } {
  if (period === 'all') return {};
  const todayIso = toIsoDate(today);
  if (period === 'ytd') return { from: `${today.getFullYear()}-01-01`, to: todayIso };
  if (period === '12m') {
    const d = new Date(today);
    d.setFullYear(d.getFullYear() - 1);
    d.setDate(d.getDate() + 1);
    return { from: toIsoDate(d), to: todayIso };
  }
  const year = Number(period);
  const yearEnd = `${year}-12-31`;
  return { from: `${year}-01-01`, to: yearEnd > todayIso ? todayIso : yearEnd };
}

/** Чистый денежный результат периода по купонам/выплатам: брутто − НКД уплаченный + НКД полученный (без налогов). */
export function periodNetCoupons(p: IncomePeriod): number {
  return p.couponsGrossRub + p.distributionsGrossRub - Math.abs(p.accruedPaidRub) + Math.abs(p.accruedReceivedRub);
}

export const ASSET_KIND_LABEL: Record<IncomeAssetFilter, string> = { all: 'Все', bond: 'Облигации', fund: 'Фонды' };

export const STATUS_LABEL: Record<IncomeStatus, string> = { open: 'Открыта', closed: 'Закрыта', redeemed: 'Погашена' };

/** Человеческие названия операций журнала; «income» у облигации — купон, у фонда — выплата фонда. */
export function ledgerKindLabel(kind: IncomeLedgerKind, assetKind: 'bond' | 'fund'): string {
  switch (kind) {
    case 'buy':
      return 'Покупка';
    case 'sell':
      return 'Продажа';
    case 'income':
      return assetKind === 'fund' ? 'Выплата фонда' : 'Купон';
    case 'tax':
      return 'Налог';
    case 'fee':
      return 'Комиссия';
    case 'principalReturn':
      return 'Амортизация';
    case 'redemption':
      return 'Погашение';
    case 'securityIn':
      return 'Ввод бумаг';
    case 'securityOut':
      return 'Вывод бумаг';
  }
}

/** CSS-класс знака суммы (токены кита через app.css). */
export function signClass(v: number | null | undefined): string {
  if (v === null || v === undefined || Math.abs(v) < 0.005) return '';
  return v > 0 ? 'bonds-positive' : 'bonds-negative';
}

/** Сумма со знаком «+» для положительных (результаты и потоки). */
export function formatSignedRub(v: number | null | undefined, fmt: (n: number | null | undefined) => string): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '—';
  return v > 0.005 ? `+${fmt(v)}` : fmt(v);
}

// ── Сортировка таблицы ──────────────────────────────────────────────────────────

export type IncomeSortKey =
  | 'name'
  | 'holding'
  | 'invested'
  | 'coupons'
  | 'realized'
  | 'unrealized'
  | 'costs'
  | 'periodNet'
  | 'total'
  | 'xirr';

export interface IncomeSortState {
  key: IncomeSortKey;
  dir: 'asc' | 'desc';
}

export const DEFAULT_INCOME_SORT: IncomeSortState = { key: 'total', dir: 'desc' };

export function positionKey(p: Pick<IncomePosition, 'assetKind' | 'assetId'>): string {
  return `${p.assetKind}:${p.assetId}`;
}

export function positionTitle(p: IncomePosition): string {
  return p.name ?? p.ticker ?? p.isin ?? `#${p.assetId}`;
}

/** Налоги + комиссии: all-time или за период. */
export function costsOf(p: IncomePosition, periodMode: boolean): number {
  return periodMode ? p.periodTaxesRub + p.periodFeesRub : p.taxesRub + p.feesRub;
}

export function couponsOf(p: IncomePosition, periodMode: boolean): number {
  return periodMode ? p.periodIncomeGrossRub : p.netCouponIncomeRub;
}

export function realizedOf(p: IncomePosition, periodMode: boolean): number {
  return periodMode ? p.periodRealizedPnlRub : p.realizedPnlRub;
}

/** Денежный результат за период по позиции (без НКД и бумажной прибыли — они не режутся периодом по позиции). */
export function periodResultOf(p: IncomePosition): number {
  return p.periodIncomeGrossRub + p.periodRealizedPnlRub + p.periodTaxesRub + p.periodFeesRub;
}

function sortValue(p: IncomePosition, key: IncomeSortKey, periodMode: boolean): number | string | null {
  switch (key) {
    case 'name':
      return positionTitle(p).toLowerCase();
    case 'holding':
      return p.holdingDays;
    case 'invested':
      return p.investedTotalRub;
    case 'coupons':
      return couponsOf(p, periodMode);
    case 'realized':
      return realizedOf(p, periodMode);
    case 'unrealized':
      return p.unrealizedPnlRub;
    case 'costs':
      return costsOf(p, periodMode);
    case 'periodNet':
      return periodResultOf(p);
    case 'total':
      return p.totalResultRub;
    case 'xirr':
      return p.xirrFraction;
  }
}

/** Сортировка: null всегда в конце независимо от направления; ничья — по имени (детерминированно). */
export function sortPositions(rows: IncomePosition[], sort: IncomeSortState, periodMode: boolean): IncomePosition[] {
  const dir = sort.dir === 'asc' ? 1 : -1;
  return [...rows].sort((a, b) => {
    const va = sortValue(a, sort.key, periodMode);
    const vb = sortValue(b, sort.key, periodMode);
    if (va === null && vb === null) return positionTitle(a).localeCompare(positionTitle(b), 'ru');
    if (va === null) return 1;
    if (vb === null) return -1;
    const cmp = typeof va === 'string' ? va.localeCompare(vb as string, 'ru') : (va as number) - (vb as number);
    return cmp !== 0 ? cmp * dir : positionTitle(a).localeCompare(positionTitle(b), 'ru');
  });
}

// ── «Водопад»: из чего сложился результат ───────────────────────────────────────

export interface BreakdownItem {
  key: string;
  label: string;
  /** Вклад компонента (со знаком). Для итога — сам итог. */
  value: number;
  isTotal: boolean;
  /** Кумулятивные границы бара (для плавающего «водопада»). */
  start: number;
  end: number;
  hint: string;
}

/**
 * Разложение `totals.totalResultRub` на компоненты. Инвариант бэкенда:
 * итог = брутто − НКД уплаченный + НКД полученный + НКД на сегодня + налоги + комиссии (с «по счёту») + продажи + бумажная прибыль.
 * Нулевые компоненты не рисуются (кроме итога).
 */
export function buildBreakdown(t: IncomeTotals, acc: IncomeAccountLevel): BreakdownItem[] {
  const raw: Array<Omit<BreakdownItem, 'start' | 'end' | 'isTotal'>> = [
    { key: 'gross', label: 'Купоны и выплаты (брутто)', value: t.incomeGrossRub, hint: 'Всё, что пришло купонами и выплатами фондов, до налога.' },
    { key: 'accruedPaid', label: 'Минус НКД, уплаченный при покупке', value: -Math.abs(t.accruedPaidRub), hint: 'Часть цены покупки, которую вы заплатили продавцу за уже накопленный купон. Эти деньги вернулись к вам первым купоном — поэтому здесь минус.' },
    { key: 'accruedReceived', label: 'Плюс НКД, полученный при продаже', value: Math.abs(t.accruedReceivedRub), hint: 'НКД, который заплатил вам покупатель при продаже бумаги.' },
    { key: 'accruedNow', label: 'Плюс накопленный НКД на сегодня', value: t.accruedNowRub, hint: 'Купон, который уже «набежал» по бумагам на руках, но ещё не выплачен.' },
    { key: 'taxes', label: 'Налоги', value: t.taxesRub + acc.taxesRub, hint: 'Удержано брокером, включая налоги «по счёту» без привязки к бумаге.' },
    { key: 'fees', label: 'Комиссии', value: t.feesRub + acc.feesRub, hint: 'Комиссии брокера, включая «по счёту» (например, за обслуживание).' },
    { key: 'realized', label: 'Прибыль от продаж и погашений', value: t.realizedPnlRub, hint: 'Результат закрытых сделок по методу средней цены, без купонов.' },
    { key: 'unrealized', label: 'Бумажная прибыль', value: t.unrealizedPnlRub, hint: 'Разница между рыночной ценой и вашей средней ценой по бумагам на руках — на сегодня, за всё время.' },
  ];
  const items: BreakdownItem[] = [];
  let cum = 0;
  for (const r of raw) {
    if (Math.abs(r.value) < 0.005) continue;
    items.push({ ...r, isTotal: false, start: cum, end: cum + r.value });
    cum += r.value;
  }
  items.push({
    key: 'total',
    label: 'Итого заработано',
    value: t.totalResultRub,
    isTotal: true,
    start: 0,
    end: t.totalResultRub,
    hint: 'Сумма всех компонентов выше.',
  });
  return items;
}

/** Ссылка на карточку: открытая облигация → /positions/:positionId, фонд → /funds/:fundId; у закрытых карточки нет. */
export function cardLinkOf(p: IncomePosition): string | null {
  if (p.assetKind === 'fund' && p.fundId !== null) return `/funds/${p.fundId}`;
  if (p.assetKind === 'bond' && p.positionId !== null) return `/positions/${p.positionId}`;
  return null;
}

/** Срок владения словами: «45 дн.», «8 мес.», «2 г. 3 мес.». */
export function formatHolding(days: number): string {
  if (days < 60) return `${days} дн.`;
  const months = Math.round(days / 30.4);
  if (months < 24) return `${months} мес.`;
  const years = Math.floor(months / 12);
  const rest = months % 12;
  return rest > 0 ? `${years} г. ${rest} мес.` : `${years} г.`;
}
