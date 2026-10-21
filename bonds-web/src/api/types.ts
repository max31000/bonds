/**
 * Общие DTO-типы API. Контракты — см. plan/08 (`src/Bonds.Api/Endpoints/*.cs`).
 */

/** Тип купона облигации (Bonds.Core.Models.CouponType, как строка после сериализации). */
export type CouponType = 'Fixed' | 'Floating' | 'Indexed';

/** Строка таблицы позиций — GET /api/positions (см. `PositionsEndpoints.PositionRowDto`). */
export interface PositionRow {
  positionId: number;
  instrumentId: number;
  name: string | null;
  isin: string | null;
  issuer: string | null;
  sector: string | null;
  quantity: number;
  marketValueRub: number;
  /** НКД на одну бумагу — уже включён в marketValueRub, показывается как разложение. */
  accruedPerBondRub: number;
  /** НКД на всю позицию = accruedPerBondRub × quantity. */
  accruedTotalRub: number;
  currencyRub: 'RUB';
  couponType: CouponType;
  maturityDate: string;
  horizonDate: string;
  calculatedToOffer: boolean;
  ytmEffective: number | null;
  currentYield: number | null;
  modifiedDuration: number | null;
  gSpread: number | null;
  isFloater: boolean;
  isIndexed: boolean;
  isEstimated: boolean;
  dataIncomplete: boolean;
  /** §11: номинал в иностранной валюте — вне рублёвого контура MVP. */
  isOutOfScopeCurrency: boolean;

  // ---- Цена входа / P&L "от цены входа" — null, если по журналу операций не посчитать. ----

  /** Средняя цена входа за бумагу (average cost). */
  averageCostRub: number | null;
  /** Вложено в текущий остаток = averageCostRub × quantity. */
  investedRub: number | null;
  /** Текущая рыночная стоимость минус вложенное. */
  unrealizedPnlRub: number | null;
  /** Доля (0.12 = 12%) — форматировать через formatPercent. */
  unrealizedPnlPercent: number | null;
  /** Сумма купонных операций по бумаге за всё время. */
  couponsReceivedRub: number | null;
  /** (unrealizedPnlRub + couponsReceivedRub) / investedRub — доля. */
  totalReturnPercent: number | null;
  /** True — журнал операций не покрывает весь текущий остаток; метрики выше приблизительны. */
  costBasisIncomplete: boolean;
}

/** Ответ GET /api/positions. */
export interface PositionsResponse {
  positions: PositionRow[];
  disclaimer: string;
}

// ---- GET /api/positions/{id} — карточка позиции/drill-down ----

/** Диапазон графика цены карточки позиции. */
export type PriceHistoryRange = '1m' | '6m' | '1y' | 'all';

/** Одна дневная точка графика цены. */
export interface PriceHistoryPoint {
  date: string;
  /** Цена закрытия, % от номинала. Null — торгов в этот день не было. */
  closePricePercent: number | null;
  accruedInterestRub: number | null;
}

/** Один купон в календаре бумаги. */
export interface CouponScheduleItem {
  couponDate: string;
  /** Размер купона на один номинал. Null — неизвестен (флоатер, далёкий горизонт). */
  valueRub: number | null;
  /** valueRub × количество бумаг в позиции. */
  valueForPositionRub: number | null;
  isKnown: boolean;
  isPast: boolean;
}

/** Одна амортизация в календаре бумаги. */
export interface AmortizationScheduleItem {
  date: string;
  /** Null — сумма неизвестна (MBS/ипотечный агент, isKnown=false); не путать с реальным нулём. */
  amountRub: number | null;
  /** Null — сумма неизвестна (isKnown=false). */
  amountForPositionRub: number | null;
  isKnown: boolean;
  isPast: boolean;
}

/** Одна оферта в календаре бумаги. */
export interface OfferScheduleItem {
  date: string;
  offerType: 'Put' | 'Call';
  isExecuted: boolean;
  isPast: boolean;
}

/** Одна операция журнала пользователя по инструменту. */
export interface OperationItem {
  id: number;
  type: 'Buy' | 'Sell' | 'Coupon' | 'Amortization' | 'Redemption' | 'Tax' | 'Fee';
  date: string;
  amountRub: number;
  quantity: number | null;
}

/** «Если продать сейчас» — GET /api/positions/{id} → ifSoldNow. */
export interface IfSoldNow {
  marketValueRub: number;
  /** marketValueRub − accruedTotalRub — «чистая» стоимость остатка без НКД. */
  cleanValueRub: number;
  /** НКД на всю позицию, уже включённый в marketValueRub (разложение). */
  accruedTotalRub: number;
  commissionRub: number;
  commissionRate: number;
  /** Источник commissionRate (override/оценка из журнала/дефолт). */
  commissionRateSource: CommissionRateSource;
  netProceedsRub: number;
  realizedPnlRub: number | null;
  realizedPnlPercent: number | null;
  couponsReceivedRub: number | null;
  totalReturnWithCouponsRub: number | null;
  pnlAvailable: boolean;
  /** Оценка НДФЛ с продажи (13% с прибыли к средней цене входа) — null, если журнал операций неполон/cost basis недоступен. */
  taxEstimateRub: number | null;
  /** totalReturnWithCouponsRub − taxEstimateRub — итог после налога. Null, если taxEstimateRub недоступен. */
  netAfterTaxRub: number | null;
  disclaimer: string;
}

/** Ответ GET /api/positions/{id} — детальная карточка позиции/инструмента. */
export interface PositionDetail {
  positionId: number;
  instrumentId: number;
  isin: string;
  name: string | null;
  issuer: string | null;
  sector: string | null;
  quantity: number;
  faceValue: number;
  currency: string;
  couponType: CouponType;
  maturityDate: string;
  horizonDate: string;
  calculatedToOffer: boolean;
  hasAmortization: boolean;
  hasOffers: boolean;

  cleanPrice: number;
  /** НКД на одну бумагу. */
  accruedInterest: number;
  /** НКД на всю позицию = accruedInterest × quantity. */
  accruedTotalRub: number;
  dirtyPrice: number;
  marketValueRub: number;

  ytmEffective: number | null;
  ytmSimple: number | null;
  currentYield: number | null;
  macaulayDuration: number | null;
  modifiedDuration: number | null;
  convexity: number | null;
  pvbp: number | null;
  gSpread: number | null;

  isFloater: boolean;
  isIndexed: boolean;
  isEstimated: boolean;
  dataIncomplete: boolean;
  isOutOfScopeCurrency: boolean;
  notes: string[];

  averageCostRub: number | null;
  investedRub: number | null;
  unrealizedPnlRub: number | null;
  unrealizedPnlPercent: number | null;
  couponsReceivedRub: number | null;
  totalReturnPercent: number | null;
  costBasisIncomplete: boolean;

  priceHistory: PriceHistoryPoint[];
  couponSchedule: CouponScheduleItem[];
  amortizationSchedule: AmortizationScheduleItem[];
  offerSchedule: OfferScheduleItem[];
  operations: OperationItem[];
  ifSoldNow: IfSoldNow;

  disclaimer: string;
}

// ---- GET /api/cashflow ----

/** Тип денежного потока, освобождающего тело долга. */
export type PrincipalFlowType = 'Amortization' | 'Maturity' | 'Offer' | 'Call';

/** Разбивка потока за месяц по одной позиции и типу (drill-down, план/11 A4). */
export interface PositionFlowInMonth {
  positionId: number;
  name: string | null;
  issuer: string | null;
  flowType: string;
  grossRub: number;
  taxRub: number;
  netRub: number;
  isEstimated: boolean;
}

/** Агрегат денежного потока по календарному месяцу. */
export interface CashflowMonth {
  month: string;
  grossRub: number;
  taxRub: number;
  netRub: number;
  couponGrossRub: number;
  principalGrossRub: number;
  hasEstimatedFlows: boolean;
  positions: PositionFlowInMonth[];
}

/** Агрегат денежного потока по позиции. */
export interface CashflowByPosition {
  positionId: number;
  instrumentId: number;
  name: string | null;
  issuer: string | null;
  grossRub: number;
  taxRub: number;
  netRub: number;
  hasEstimatedFlows: boolean;
}

/** Дата освобождения тела долга (амортизация/погашение/оферта/колл). */
export interface PrincipalRelease {
  date: string;
  positionId: number;
  instrumentId: number;
  flowType: PrincipalFlowType;
  amountRub: number;
  isEstimated: boolean;
}

/** Одно из ближайших поступлений (GET /api/cashflow → nextPayments, task C1). */
export interface NextPayment {
  date: string;
  name: string | null;
  issuer: string | null;
  flowType: string;
  netRub: number;
  isEstimated: boolean;
}

/** Ответ GET /api/cashflow. */
export interface CashflowResponse {
  byMonth: CashflowMonth[];
  byPosition: CashflowByPosition[];
  principalReleases: PrincipalRelease[];
  nextPayments: NextPayment[];
  disclaimer: string;
}

// ---- GET /api/analytics/scatter ----

/**
 * Вид доходности, использованной для точки на scatter-графике/сравнении. Зеркалит
 * сериализацию бэкенд-enum `YieldKind.ToString()` — значение `CurrentYield`, НЕ `Current`
 * (задача 32 часть A: до фикса фронт-тип `'Current'` никогда не совпадал со строкой от сервера,
 * из-за чего ветка «вне сравнения» по yieldKind не срабатывала в проде, см.
 * `PositionComparisonService.cs`/`AnalyticsEndpoints.cs`).
 */
export type YieldKind = 'Ytm' | 'CurrentYield';

/** Точка позиции на графике «дюрация × доходность». */
export interface ScatterPoint {
  positionId: number;
  instrumentId: number;
  name: string | null;
  issuer: string | null;
  modifiedDuration: number;
  /** дюрация Маколея — по ней строится ось X scatter (согласована с G-спредом). */
  macaulayDuration: number;
  effectiveYield: number;
  yieldKind: YieldKind;
  isFloater: boolean;
  isIndexed: boolean;
  isEstimated: boolean;
  dataIncomplete: boolean;
  /** true — точка watchlist-бумаги без позиции (полый маркер на графике). */
  isWatchlist: boolean;
}

/** Точка реконструированной безрисковой кривой. */
export interface CurvePoint {
  termYears: number;
  yield: number;
}

/** Ответ GET /api/analytics/scatter. */
export interface ScatterResponse {
  points: ScatterPoint[];
  curve: CurvePoint[];
  curveAsOf: string | null;
  disclaimer: string;
}

// ---- GET /api/analytics/composition ----

/** Доля в общей композиции портфеля по одному из разрезов. */
export interface CompositionSlice {
  key: string;
  marketValueRub: number;
  sharePercent: number;
}

/** Ответ GET /api/analytics/composition. */
export interface CompositionResponse {
  totalMarketValueRub: number;
  byIssuer: CompositionSlice[];
  bySector: CompositionSlice[];
  byCouponType: CompositionSlice[];
  byDurationBucket: CompositionSlice[];
  disclaimer: string;
}

// ---- GET /api/analytics/xirr ----

/** Точка истории доходности портфеля (XIRR) во времени. */
export interface XirrHistoryPoint {
  date: string;
  marketValueRub: number;
  investedRub: number;
  xirr: number;
}

/** Ответ GET /api/analytics/xirr. */
export interface XirrResponse {
  currentXirr: number | null;
  history: XirrHistoryPoint[];
  disclaimer: string;
}

/** Ответ POST /api/analytics/xirr/backfill — сколько новых точек истории записано. */
export interface XirrBackfillResponse {
  pointsWritten: number;
}

// ---- GET /api/signals, POST /api/signals/{id}/read, POST /api/signals/read-all («События») ----

/** Важность события — буквально enum бэка `SignalSeverity`. */
export type SignalSeverity = 'Info' | 'Warning' | 'Critical';

/** Тип события — буквально enum бэка `SignalType` (купоны/дюрация/доходность из событий убраны). */
export type SignalType =
  | 'UpcomingAmortization'
  | 'UpcomingRedemption'
  | 'UpcomingOffer'
  | 'FloaterRateReset'
  | 'UninvestedCashThreshold'
  | 'ConcentrationLimitBreached'
  // Задача 49: рыночные и кредитные события.
  | 'SpreadWidening'
  | 'PriceDrawdown'
  | 'IssuerDefaultEvent';

/**
 * Событие портфеля, требующее внимания (бэкенд `SignalDto`). Это текущее состояние, а не лог:
 * `eventDate` — дата события (оферта/погашение/ресет), null для «состояний» (концентрация, свободные деньги).
 * Даты-время (`firstSeenAt`, `resolvedAt`) — UTC ISO.
 */
export interface Signal {
  id: number;
  type: SignalType;
  severity: SignalSeverity;
  instrumentId: number | null;
  /** Имя бумаги либо эмитента. */
  subject: string | null;
  suggestedAction: string | null;
  eventDate: string | null;
  firstSeenAt: string;
  /** Подтверждено владельцем («прочитано»). */
  isRead: boolean;
  /** Движок перестал производить событие (история). */
  isResolved: boolean;
  resolvedAt: string | null;
}

/** Ответ GET /api/signals. */
export interface SignalsResponse {
  signals: Signal[];
}

/** Ответ POST /api/signals/{id}/read. */
export interface SignalReadResponse {
  id: number;
  isRead: boolean;
}

/** Ответ POST /api/signals/read-all. */
export interface SignalsReadAllResponse {
  acknowledged: number;
}

// ---- POST /api/sync, GET /api/sync/status ----

/** Ответ POST /api/sync — результат единичного цикла форс-синхронизации. */
export interface SyncRunResult {
  alreadyRunning: boolean;
  noAccountConfigured: boolean;
  /** токен T-Invest не задан или не расшифровался — синк деградировал на этом шаге. */
  tokenMissingOrInvalid: boolean;
  instrumentsSynced: number;
  operationsUpserted: number;
  yieldCurveUpdated: boolean;
  positionsProjected: number;
  flowsWritten: number;
  snapshotStored: boolean;
  signalsCreated: number;
  errors: string[];
  hasErrors: boolean;
}

/** Ответ GET /api/sync/status — текущий статус планировщика синхронизации. */
export interface SyncStatus {
  isRunning: boolean;
  lastRunStartedAtUtc: string | null;
  lastSuccessAtUtc: string | null;
  lastFailureAtUtc: string | null;
  lastRunErrors: string[];
  /** токен T-Invest не задан или не расшифровался (см. SyncRunResult). */
  tokenMissingOrInvalid: boolean;
}

// ---- GET /api/analytics/rate-scenario ----

export interface RateScenarioPoint {
  shiftBp: number;
  newValueRub: number;
  deltaRub: number;
  deltaPercent: number;
}

export interface RateScenarioResponse {
  currentValueRub: number;
  /** H-1/M-1: процентно-чувствительная часть портфеля (бумаги с дюрацией) — база для Δ. */
  rateSensitiveValueRub: number;
  scenarios: RateScenarioPoint[];
  disclaimer: string;
}

// ---- GET /api/analytics/trajectory ----

export interface TrajectoryPoint {
  month: string;
  portfolioValueRub: number;
  cumulativeIncomeRub: number;
}

export interface TrajectoryResponse {
  initialValueRub: number;
  withReinvest: TrajectoryPoint[];
  withoutReinvest: TrajectoryPoint[];
  reinvestRateUsed: number;
  disclaimer: string;
}

// ---- GET /api/analytics/comparison, POST /api/analytics/replacement ----

/** Строка таблицы сравнения позиций — GET /api/analytics/comparison. */
export interface ComparisonRow {
  positionId: number;
  instrumentId: number;
  name: string | null;
  issuer: string | null;
  effectiveYield: number | null;
  yieldKind: YieldKind;
  modifiedDuration: number | null;
  /** Дюрация Маколея, ГОДЫ (задача 44) — единая мера для сравнения с корзинами/кандидатами банка
   * (банк хранит MOEX DURATION = Маколей); modifiedDuration остаётся мерой чувствительности. */
  macaulayDurationYears: number | null;
  gSpread: number | null;
  daysToHorizon: number;
  horizonDate: string;
  calculatedToOffer: boolean;
  couponType: CouponType;
  isEstimated: boolean;
  dataIncomplete: boolean;
}

/** Ответ GET /api/analytics/comparison. */
export interface ComparisonResponse {
  rows: ComparisonRow[];
  disclaimer: string;
}

/** Тело POST /api/analytics/replacement. */
export interface ReplacementRequest {
  holdPositionId: number;
  /** Target — позиция портфеля. Игнорируется на бэке, если задан targetInstrumentId. */
  targetPositionId?: number;
  /** target — watchlist-бумага без позиции, по InstrumentId. */
  targetInstrumentId?: number;
  /** Горизонт, ГОДЫ. Не передан — бэкенд берёт настройку пользователя (replacementHorizonYears). */
  horizonYears?: number;
  sellCommissionRate?: number;
  buyCommissionRate?: number;
}

/** Ответ POST /api/analytics/replacement — «держать A vs переложиться в B» (SwitchAnalysisService). */
export interface ReplacementResponse {
  holdPositionId: number;
  targetPositionId: number;
  /** Эхо запроса — задан, только если target был watchlist-бумагой без позиции. */
  targetInstrumentId?: number | null;
  horizonYears: number;
  sellCommissionRub: number;
  buyCommissionRub: number;
  /** Комиссии обеих сделок (продажа + покупка), рубли; слиппедж и налог — отдельные поля. */
  totalSwitchCostRub: number;
  /** FV_switch − FV_hold БЕЗ налога продажи (но после НДФЛ на купоны, комиссий и слиппеджа), рубли. */
  netBenefitRub: number;
  isSwitchFavorable: boolean;
  breakEvenYears: number | null;
  yieldDataIncomplete: boolean;
  disclaimer: string;

  /** Фактически применённая ставка продажи/покупки — ДОЛЯ. */
  sellCommissionRateUsed: number;
  buyCommissionRateUsed: number;
  /** Источник ставки — CommissionRateSource либо "ExplicitRequest" (обе ставки заданы явно в запросе). */
  commissionRateSource: CommissionRateSource | 'ExplicitRequest';

  /** Та же формула-разбивка, что MatrixPair — спред эффективных доходностей (targetYield − holdYield), ДОЛЯ. Null — yieldDataIncomplete. */
  spreadFraction: number | null;
  /** Капитал, реально вложенный в цель: выручка hold минус комиссия/слиппедж покупки (и минус комиссия, слиппедж, налог продажи — до этого), рубли. */
  capitalRub: number;
  /** Валовая выгода: MV × ((1+y_target_at)^h − (1+y_hold_at)^h) — разница доходностей после НДФЛ на полной стоимости hold ДО издержек, рубли. Null — yieldDataIncomplete. */
  grossGainRub: number | null;
  /** (FV_switch / FV_hold)^(1/effectiveHorizonYears) − 1 — годовая выгода ДОЛЕЙ (не процент). Null — yieldDataIncomplete. */
  annualizedBenefitFraction: number | null;
  /** Оценка НДФЛ от продажи hold-позиции — null, если cost basis недоступен/журнал неполон. */
  sellTaxEstimateRub: number | null;
  /** FV_switch − FV_hold С налогом продажи (он уже вычтен из выручки, поэтому разница с netBenefitRub больше самого налога — налог ещё и «не растёт» на горизонте). Null, если sellTaxEstimateRub недоступен. */
  netBenefitAfterTaxRub: number | null;
  /** Риск-сигналы таргета (см. <see>RiskSignals</see>) — только если таргет
   * найден в банке (bond_universe) по ISIN, иначе null (бумага вне биржевой статистики MOEX). */
  targetRiskSignals?: RiskSignals | null;

  // ---- Задача 48: математика замены v2 (additive). Доходности — ДОЛИ, деньги — рубли, горизонты/дюрации — годы. ----

  /** Откуда взят horizonYears: 'Request' (явно в запросе) | 'Settings' (настройка пользователя). */
  horizonSource: 'Request' | 'Settings';
  /** Горизонт, на котором сравнены ноги: min(horizonYears, срок до оферты/погашения цели), не меньше 0.25 года. */
  effectiveHorizonYears: number;
  /** true — горизонт урезан сроком цели; тогда заданы targetHorizonDate/targetHorizonIsOffer. */
  horizonLimitedByTarget: boolean;
  /** Дата оферты/погашения цели, обрезавшая горизонт (ISO yyyy-MM-dd). */
  targetHorizonDate?: string | null;
  /** true — targetHorizonDate это оферта, false — погашение. */
  targetHorizonIsOffer: boolean;
  /** Ставка НДФЛ на купоны/дисконт — ДОЛЯ (0.13). Упрощение: ко всей доходности, без сальдирования/ЛДВ. */
  couponTaxRate: number;
  /** Доходность hold/цели после НДФЛ — ДОЛЯ. */
  holdYieldAfterTaxFraction: number;
  targetYieldAfterTaxFraction: number;
  /** Слиппедж продажи/покупки (половина спреда стакана), рубли; 0 при slippageUnknown. */
  sellSlippageRub: number;
  buySlippageRub: number;
  /** true — у одной из ног стакан (bid/offer) неизвестен, слиппедж этой ноги НЕ учтён. */
  slippageUnknown: boolean;
  /** Выручка продажи hold после комиссии, слиппеджа и налога продажи, рубли. */
  proceedsRub: number;
  /** Будущая стоимость hold / варианта «переложиться» на эффективном горизонте, рубли. */
  holdFutureValueRub: number;
  switchFutureValueRub: number;
  /** Дюрации Маколея hold/цели, ГОДЫ (null — не посчитаны). */
  holdMacaulayDurationYears?: number | null;
  targetMacaulayDurationYears?: number | null;
  /** |Маколей цели − Маколей hold| > 1.5 года. */
  durationMismatch: boolean;
  /** «Процентный риск изменится: дюрация X → Y лет» при durationMismatch. */
  durationMismatchWarning?: string | null;
}

// ---- GET /api/analytics/replacement-candidates (фронт — задача 35 §A) ----

/** Уровень информационного риск-сигнала кандидата — НЕ рейтинг кредитного качества/агентства. */
export type RiskSignalLevel = 'Good' | 'Neutral' | 'Caution';

/**
 * Светофор надёжности v2 (задача 46): результат скоркарты (структура выпуска + отчётность эмитента + динамика
 * рынка), а не оси спреда. Зеркалит backend `ReliabilityLight`. <b>НЕ кредитный
 * рейтинг</b> — оценка по открытым данным, см. `RiskSignalsCaption`/тултип
 * `RiskSignalBadges` для обязательного дисклеймера.
 */
export type ReliabilityLevel = 'Green' | 'Yellow' | 'Red';

/**
 * Два ИНФОРМАЦИОННЫХ риск-сигнала кандидата-замены (ликвидность+листинг, отклонение спреда от
 * медианы его корзины) — зеркалит `RiskSignalsDto`. Не ранжирует кандидатов
 * (ранжирование mode=market идёт по доходности) и НЕ является рейтингом агентств.
 */
export interface RiskSignals {
  liquidity: RiskSignalLevel;
  /** Человекочитаемая подпись, напр. "Высокая ликвидность, листинг 1". */
  liquidityLabel: string;
  spread: RiskSignalLevel;
  /** ДОЛЯ; эхо G-спреда кандидата. Null, если MOEX не отдал спред. */
  gSpreadFraction: number | null;
  /** ДОЛЯ; gSpreadFraction − медиана корзины кандидата. Положительное — спред выше медианы. */
  spreadVsBasketMedianFraction: number | null;
  /** Светофор надёжности — см. `ReliabilityLevel`. */
  reliability: ReliabilityLevel;
  /** Человекочитаемое обоснование `reliability` — задача 38 часть A.2. */
  reliabilityReason: string;
  /**
   * Задача 46 (скоркарта надёжности v2; UI-разбивка — задача 47). Поля additive, необязательны в типе, чтобы не
   * ломать фикстуры: бэкенд отдаёт их всегда. Светофор `reliability` теперь считает скоркарта, а НЕ ось спреда.
   */
  /** Скор 0–100 по компонентам с данными. НЕ из G-спреда. */
  score?: number;
  /** Покрытие данными 0–1 (низкое — скор опирается на мало данных). */
  coverage?: number;
  /** Разбивка скоркарты: каждый компонент с источником и признаком «нет данных». */
  components?: ScoreComponent[];
  /** Критические флаги: IssuerDefaulted | InterestUncovered | NegativeEquity | SpreadShock. */
  criticalFlags?: string[];
  /** Предупреждения (не краснят светофор): IssuerTechDefault | PriceDrawdown | JunkRating. */
  warningFlags?: string[];
  /** Ось Value (цена, НЕ риск) — отдельно от `reliability`. */
  valueVerdict?: ValueVerdict;
  /** Задача 47: ДОЛЯ (0.003 = 30 б.п.) — медиана G-спреда корзины бумаги, для тултипа Value; null — у бумаги нет спреда. */
  valueMedianFraction?: number | null;
  /** Задача 47: ДОЛЯ — порог вердикта Value, max(20 б.п., 0.25 × IQR корзины); null вместе с медианой. */
  valueThresholdFraction?: number | null;
}

/** Ось Value: G-спред бумаги против медианы её корзины. `InsufficientData` — корзина нерепрезентативна. */
export type ValueVerdict = 'Cheap' | 'Fair' | 'Rich' | 'InsufficientData';

/** Один компонент скоркарты надёжности — зеркалит `ScoreComponentDto`. */
export interface ScoreComponent {
  /** issuer_default_history | listing | no_offer | not_qual_only | broker_risk_level | manual_rating | revenue_size | interest_coverage | leverage | current_ratio | trend | altman_z_em | sovereign | spread_momentum | price_drawdown | liquidity. */
  key: string;
  title: string;
  /** Число показателя (ДОЛЯ для spread_momentum/price_drawdown, РУБЛИ для revenue_size, «раз» для коэффициентов). */
  rawValue: number | null;
  /** Значение/причина словами, напр. «2200/2330 = 2.4×» или «нет данных: …». */
  rawDisplay: string | null;
  points: number;
  maxPoints: number;
  source: string;
  /** `true` — данных нет: компонент не входит в скор (в отличие от честного нуля). */
  missing: boolean;
  /** `A` структурный | `B` фундаментальный | `C` рыночный. */
  layer: 'A' | 'B' | 'C';
  /** `true` — отчётность старше 20 месяцев. */
  stale: boolean;
}

/** Режим подбора кандидатов-замен для одной позиции портфеля (GET /replacement-candidates). */
export type ReplacementCandidatesMode = 'market' | 'rv';

/** Задача 50: сопоставимость кандидатов по сроку — "bucket" (бакет дюрации Маколея позиции ±1 соседний) / "all" (весь рынок). */
export type DurationScope = 'bucket' | 'all';

/** Задача 50: к чему считана доходность кандидата (худший сценарий): оферта или погашение. */
export type YieldBasis = 'offer' | 'maturity';

/** Один кандидат-замена — дешёвая банк-статистика (bond_universe), НЕ точный расчёт движка (см.
 * doc-comment backend `ReplacementCandidateDto`) — точную выгоду считает POST /replacement. */
export interface ReplacementCandidate {
  secid: string;
  isin: string | null;
  name: string;
  /** Банк-слой не хранит эмитента — всегда null. */
  issuer: string | null;
  sector: string | null;
  /** ДОЛЯ; сырая биржевая доходность банка (MOEX YIELD), ДО налога. */
  yieldFraction: number | null;
  /** Задача 50, ДОЛЯ: доходность к худшему из погашения/оферты ПОСЛЕ НДФЛ — ключ сортировки mode=market
   * (`min(YTM, к оферте) × (1 − ставка)`, ставка — `ReplacementCandidatesResponse.afterTaxRateFraction`). Null — доходности нет. */
  afterTaxYieldFraction: number | null;
  /** Задача 50: к чему считана afterTaxYieldFraction — "offer" (к оферте, дата — `offerDate`) | "maturity" (к погашению). */
  yieldBasis: YieldBasis | null;
  /** Годы. */
  durationYears: number | null;
  /** ДОЛЯ; G-спред банка: биржевой, а при его отсутствии приближённый по кривой. */
  gSpreadFraction: number | null;
  /** Дата оферты (ISO "2028-01-01"), если есть — доходность к оферте считается
   * движком на другом горизонте, чем к погашению. Null — оферты нет/не известна. */
  offerDate: string | null;
  riskSignals: RiskSignals;
}

/** Ответ GET /api/analytics/replacement-candidates. */
export interface ReplacementCandidatesResponse {
  /** "market" | "rv" — эхо запрошенного режима. */
  mode: string;
  positionIsin: string;
  candidates: ReplacementCandidate[];
  disclaimer: string;
  /** Задача 50, только mode=market: ПРИМЕНЁННЫЙ scope по сроку. Может отличаться от запрошенного: у позиции нет дюрации →
   * бакет не определить, бэкенд откатывается на "all". Null для mode=rv. */
  durationScope?: DurationScope | null;
  /** Задача 50: метка бакета дюрации позиции ("1–3 года", Маколей); null — дюрация позиции неизвестна. */
  positionDurationBucket?: string | null;
  /** Задача 50, ДОЛЯ (0.13 = 13%): НДФЛ, учтённый в afterTaxYieldFraction кандидатов. */
  afterTaxRateFraction?: number;
}

// ---- GET /api/analytics/allocation (задача 34 — source=market/recommended) ----

/** Причина, по которой кандидат не получил докупку. Зеркалит CashAllocationSkipReason бэкенда. */
export type AllocationSkipReason = 'NoYield' | 'ConcentrationLimit' | 'NoPrice' | 'NotComparable' | 'InsufficientFunds';

/** Источник кандидатов аллокации — эхо запроса/выбор пользователя. */
export type AllocationSource = 'portfolio' | 'market' | 'recommended';

/** Одна строка распределения — сколько купить конкретной бумаги. */
export interface AllocationLine {
  /** null для Source=market/recommended (банк-кандидат не связан с Instrument) —
   * идентификатор таких строк — {@link AllocationLine.secid}. */
  instrumentId: number | null;
  /** Биржевой SECID — только для Source=market/recommended. Null для Source=portfolio. */
  secid: string | null;
  name: string | null;
  issuer: string | null;
  /** Грубая секторная классификация банка — заполнена для Source=market/recommended. */
  sector: string | null;
  quantity: number;
  estimatedCostRub: number;
  effectiveYield: number;
  lotSizeAssumed: boolean;
  /** Разложение estimatedCostRub — чистая цена всей докупки (без НКД/комиссии). cleanCostRub + accruedCostRub + commissionCostRub = estimatedCostRub. */
  cleanCostRub: number;
  /** НКД в составе estimatedCostRub. */
  accruedCostRub: number;
  /** Комиссия покупки в составе estimatedCostRub. */
  commissionCostRub: number;
  /** Риск-сигналы — заполнены для Source=market/recommended, null для Source=portfolio. */
  riskSignals: RiskSignals | null;
}

/** Кандидат, не получивший докупку, и причина. */
export interface AllocationSkip {
  /** Null для Source=market/recommended — см. doc-comment {@link AllocationLine.instrumentId}. */
  instrumentId: number | null;
  /** См. {@link AllocationLine.secid}. */
  secid: string | null;
  name: string | null;
  issuer: string | null;
  reason: AllocationSkipReason;
}

/** Ответ GET /api/analytics/allocation. */
export interface AllocationResponse {
  amountRub: number;
  /** Эхо запрошенного источника кандидатов. */
  source: string;
  allocations: AllocationLine[];
  skipped: AllocationSkip[];
  leftoverRub: number;
  disclaimer: string;

  /** Ставка комиссии покупки, применённая к цене лота — ДОЛЯ. */
  commissionRateUsed: number;
  commissionRateSource: CommissionRateSource;

  /** Только для Source=market/recommended — сколько кандидатов реально
   * подходило до отсечения потолком. Null для Source=portfolio. */
  candidatePoolAvailable: number | null;
  /** Верхняя граница числа кандидатов, участвовавших в жадном проходе. Null для Source=portfolio. */
  candidatePoolLimit: number | null;
  /** true — потолок реально обрезал часть подходящих кандидатов. Null для Source=portfolio. */
  candidatePoolTruncated: boolean | null;
}

// ---- POST /api/analytics/basket ----

/** Тело запроса POST /api/analytics/basket. */
export interface BasketRequest {
  amountRub: number;
  lines: BasketRequestLine[];
}

/** Одна строка запроса — доля (0, 1], НЕ проценты (конвенция репо: бэкенд — доли). */
export interface BasketRequestLine {
  instrumentId: number;
  weightFraction: number;
}

/** Одна строка собранной корзины — штук, стоимость с разбивкой, факт. вес vs целевой. */
export interface BasketLine {
  instrumentId: number;
  name: string | null;
  issuer: string | null;
  targetWeightFraction: number;
  actualWeightFraction: number;
  quantity: number;
  actualCostRub: number;
  effectiveYield: number | null;
  modifiedDuration: number | null;
  isFloater: boolean;
  lotSizeAssumed: boolean;
  cleanCostRub: number;
  accruedCostRub: number;
  commissionCostRub: number;
}

/** Метрики корзины — средневзвешенные по фактической стоимости строк. */
export interface BasketMetrics {
  totalCostRub: number;
  weightedYield: number | null;
  weightedDuration: number | null;
  /** true — хотя бы одна купленная строка-флоатер/индексируемая исключена из weightedYield (сноска в UI). */
  hasExcludedFloaters: boolean;
}

export interface BasketDto {
  amountRub: number;
  lines: BasketLine[];
  leftoverRub: number;
  metrics: BasketMetrics;
}

/** Снимок метрик портфеля в один момент (до либо после покупки корзины). */
export interface WhatIfSnapshot {
  totalValueRub: number;
  weightedYield: number | null;
  weightedDuration: number | null;
  hasExcludedFloaters: boolean;
}

/** Доля одного эмитента до и после покупки корзины. */
export interface WhatIfConcentration {
  issuer: string;
  sharePercentBefore: number;
  sharePercentAfter: number;
}

export type WhatIfWarningKind = 'ConcentrationLimitBreached' | 'NewIssuerAboveThreshold';

export interface WhatIfWarning {
  kind: WhatIfWarningKind;
  issuer: string;
  sharePercentAfter: number;
}

export interface WhatIfDto {
  before: WhatIfSnapshot;
  after: WhatIfSnapshot;
  concentrations: WhatIfConcentration[];
  warnings: WhatIfWarning[];
}

/** Ответ POST /api/analytics/basket. */
export interface BasketResponse {
  basket: BasketDto;
  whatIf: WhatIfDto;
  disclaimer: string;
}

// ---- GET/POST /api/watchlist, DELETE /api/watchlist/{id} ----

/** Строка watchlist — бумага вне текущих позиций, отслеживаемая по ISIN. Метрики — тот же расчётный путь, что у позиций. */
export interface WatchlistItem {
  id: number;
  isin: string;
  note: string | null;
  addedAtUtc: string;

  /** Null — инструмент ещё не подтянут справочником (запись только что создана). */
  instrumentId: number | null;
  name: string | null;
  issuer: string | null;
  sector: string | null;
  couponType: CouponType | null;
  maturityDate: string | null;
  horizonDate: string | null;
  calculatedToOffer: boolean | null;
  modifiedDuration: number | null;
  macaulayDuration: number | null;
  ytmEffective: number | null;
  currentYield: number | null;
  /** YTM либо CurrentYield для флоатера/индексируемой — то же правило, что у позиций. */
  effectiveYield: number | null;
  gSpread: number | null;
  isFloater: boolean | null;
  isIndexed: boolean | null;
  isEstimated: boolean | null;
  dataIncomplete: boolean;
}

/** Ответ GET /api/watchlist. */
export interface WatchlistResponse {
  items: WatchlistItem[];
  disclaimer: string;
}

/** Тело POST /api/watchlist. */
export interface WatchlistCreateRequest {
  isin: string;
  note?: string;
}

/** Ответ POST /api/watchlist. */
export interface WatchlistCreateResponse {
  id: number;
  isin: string;
  note: string | null;
  addedAtUtc: string;
}

// ---- GET/PUT /api/settings, PUT /api/settings/tinvest-token ----

/** Read-only авто-оценка ставки комиссии из журнала операций. Null — журнал не позволяет оценить (нет сделок/оборот 0). */
export interface CommissionAutoEstimate {
  /** Оценённая ставка — ДОЛЯ (0.00046 = 0.046%), не процент. */
  rate: number;
  turnoverRub: number;
  feeTotalRub: number;
  tradeCount: number;
  windowMonths: number;
}

/** Источник эффективной ставки комиссии. */
export type CommissionRateSource = 'UserOverride' | 'EstimatedFromTrades' | 'Default';

/** Настройки пользователя — пороги правил событий и базовая валюта. */
export interface SettingsResponse {
  baseCurrency: 'RUB';
  tInvestTokenConfigured: boolean;
  tInvestTokenMasked: string | null;
  upcomingEventDaysThreshold: number;
  uninvestedCashThresholdRub: number;
  defaultMaxConcentrationPercent: number;

  /** Ручной override ставки комиссии — ДОЛЯ (0.0005 = 0.05%). Null — не задан. UI вводит в %, конвертация на границе API. */
  commissionRateOverride: number | null;
  /** Горизонт анализа замены, ГОДЫ (0.5–10, шаг 0.5; дефолт 2) — задача 48. */
  replacementHorizonYears: number;
  /** Порог события «расширение G-спреда» — ДОЛЯ (0.003 = 30 б.п. по умолчанию), задача 49. UI вводит в б.п., конвертация на границе API. */
  spreadWideningThresholdFraction: number;
  /** Порог события «просадка цены» — ДОЛЯ (0.05 = 5%), задача 49. UI вводит в %, конвертация на границе API. */
  priceDrawdownThresholdFraction: number;
  /** Read-only: авто-оценка ставки из журнала операций (часть A). Null — журнал не позволяет оценить. */
  commissionAutoEstimate: CommissionAutoEstimate | null;
  /** Read-only: имя тарифа T-Invest (GetInfo) — только контекст, не участвует в расчётах. Null — не удалось получить. */
  tInvestTariff: string | null;
  /** Read-only: ставка, которая реально применяется расчётами (резолвер части C) — ДОЛЯ. */
  commissionEffectiveRate: number;
  /** Read-only: источник commissionEffectiveRate. */
  commissionEffectiveSource: CommissionRateSource;
}

// ---- GET /api/universe, GET /api/universe/status, POST /api/universe/{secid}/materialize ----

/** Скор ликвидности бумаги банка (см. LiquidityScoreCalculator) — приблизительная оценка по обороту/спреду/числу
 *  сделок. Зеркалит бэкенд-enum: «нет данных» сериализуется как 'None' (не 'Unknown'). */
export type LiquidityScore = 'High' | 'Medium' | 'Low' | 'None';

/**
 * Причина, по которой гигиенический фильтр скрыл бумагу из выдачи по умолчанию (plan/26 часть
 * D) — значения зеркалят backend enum `HygieneHiddenReason` (см.
 * `src/Bonds.Core/Universe/UniverseHygieneFilter.cs`), сериализуемый как строка (`.ToString()`).
 * Исправлены на фактические значения enum — было `MissingYield` (никогда не
 * отправляется backend'ом), не хватало `ListLevelThree`/`ImplausibleYield`.
 */
export type UniverseHiddenReason =
  | 'LowTurnover'
  | 'ListLevelThree'
  | 'ImplausibleYield'
  | 'MissingDurationOrPrice'
  | 'NearMaturity';

/** Источник G-спреда строки банка (задача 42): `moex` — посчитан биржей, `approx` — наше приближение по кривой. */
export type GspreadSource = 'moex' | 'approx';

/** Одна строка банка облигаций — дешёвая биржевая статистика MOEX, НЕ точный движок (см. Disclaimer). */
export interface UniverseRow {
  secid: string;
  isin: string | null;
  name: string | null;
  sector: string | null;
  yieldFraction: number | null;
  durationYears: number | null;
  pricePercent: number | null;
  turnoverRub: number | null;
  listLevel: number | null;
  liquidityScore: LiquidityScore;
  slippageEstimateFraction: number | null;
  /**
   * ДОЛЯ. G-спред банка. Имя поля историческое (бэкенд не переименовывали): с задачи 42 это
   * ЭФФЕКТИВНЫЙ спред — биржевой (marketdata_yields), а при его отсутствии приближённый по кривой.
   * Источник — {@link UniverseRow.gspreadSource}.
   */
  gspreadApproxFraction: number | null;
  /** Задача 42: откуда `gspreadApproxFraction` — `'moex'` посчитан биржей, `'approx'` наше приближение по кривой, `null` спреда нет. */
  gspreadSource?: GspreadSource | null;
  /** Задача 42: НКД, рубли на ОДНУ бумагу (MOEX ACCRUEDINT). null — неизвестен. */
  accruedRub?: number | null;
  /** Задача 42: объём выпуска, штук бумаг. */
  issueSize?: number | null;
  /** Задача 42: ДОЛЯ. Доходность к оферте по расчёту биржи; null — оферты нет. */
  yieldToOfferFraction?: number | null;
  /** Задача 43: название эмитента (справочник MOEX); null — биржа не отдала либо обогащение ещё не отработало. Только отображение. */
  emitentTitle?: string | null;
  /** Задача 43: ИНН эмитента (строка, ведущие нули значимы); null — неизвестен. */
  emitentInn?: string | null;
  maturityDate: string | null;
  offerDate: string | null;
  isHidden: boolean;
  hiddenReason: UniverseHiddenReason | null;
  inPortfolio: boolean;
  inWatchlist: boolean;
  /**
   * Задача 31/32: эвристика по BONDTYPE MOEX — `true` флоатер, `false` фикс-купон, `null`/
   * `undefined` неизвестно (старые бумаги без BONDTYPE в кеше банка). Контракт фронта (задача 32
   * часть B): неизвестное значение считается «не флоатер» — НЕ прячется и НЕ помечается бейджем.
   */
  isFloater?: boolean | null;
  /** Светофор надёжности банк-записи — см. `ReliabilityLevel`. */
  reliability: ReliabilityLevel;
  /** Человекочитаемое обоснование `reliability`. */
  reliabilityReason: string;
  /** Задача 46: скор 0–100 скоркарты надёжности (additive; полная разбивка — GET /api/universe/{secid}/reliability). */
  score?: number;
  /** Задача 46: покрытие данными 0–1. */
  coverage?: number;
  /** Задача 46: критические флаги, краснящие светофор. */
  criticalFlags?: string[];
  /** Задача 46: ось Value отдельным полем. */
  valueVerdict?: ValueVerdict;
  /** Задача 47: ДОЛЯ — медиана G-спреда корзины (тултип Value); null — у бумаги нет спреда. */
  valueMedianFraction?: number | null;
  /** Задача 47: ДОЛЯ — порог вердикта Value; null вместе с медианой. */
  valueThresholdFraction?: number | null;
}

/** Ответ GET /api/universe. */
export interface UniverseResponse {
  rows: UniverseRow[];
  total: number;
  hiddenCount: number;
  disclaimer: string;
}

/**
 * Параметры GET /api/universe. Задача 27 использовала подмножество (поиск + сортировка по
 * доходности + лимит) для выпадашки-сравнивалки; задача 28 (страница «Скринер») добавляет
 * остальные серверные фильтры/пагинацию, зеркалящие сигнатуру `UniverseEndpoints.GetUniverse`.
 */
export interface UniverseQuery {
  search?: string;
  minYield?: number;
  maxYield?: number;
  minDurationYears?: number;
  maxDurationYears?: number;
  sector?: string;
  includeHidden?: boolean;
  /** «не хуже уровня» — green = только Green; yellow = Green+Yellow; не задан/'red' = без фильтра. */
  reliability?: 'green' | 'yellow' | 'red';
  sortBy?: 'yield' | 'duration' | 'turnover' | 'gspread';
  sortDir?: 'asc' | 'desc';
  limit?: number;
  offset?: number;
}

/** Ответ GET /api/universe/status — сводка по банку облигаций для статусной строки скринера. */
export interface UniverseStatus {
  lastRefreshUtc: string | null;
  totalBonds: number;
  hiddenBonds: number;
  historyDays: number;
}

/** Полные метрики движка для материализованной бумаги — POST /api/universe/{secid}/materialize. Те же поля, что WatchlistItem (тот же расчётный путь). */
export interface MaterializeMetrics {
  name: string | null;
  issuer: string | null;
  sector: string | null;
  couponType: CouponType;
  maturityDate: string;
  horizonDate: string;
  calculatedToOffer: boolean;
  modifiedDuration: number | null;
  macaulayDuration: number | null;
  ytmEffective: number | null;
  currentYield: number | null;
  /** YTM либо CurrentYield для флоатера/индексируемой — то же правило, что WatchlistItem. */
  effectiveYield: number | null;
  gSpread: number | null;
  isFloater: boolean;
  isIndexed: boolean;
  isEstimated: boolean;
  dataIncomplete: boolean;
}

/** Ответ POST /api/universe/{secid}/materialize. */
export interface MaterializeResponse {
  instrumentId: number;
  secid: string;
  isin: string;
  metrics: MaterializeMetrics;
  disclaimer: string;
}

/** Тело PUT /api/settings — все поля кроме токена/статуса токена и read-only контекста комиссии. */
export type SettingsUpdateRequest = Omit<
  SettingsResponse,
  | 'tInvestTokenConfigured'
  | 'tInvestTokenMasked'
  | 'commissionAutoEstimate'
  | 'tInvestTariff'
  | 'commissionEffectiveRate'
  | 'commissionEffectiveSource'
  | 'spreadWideningThresholdFraction'
  | 'priceDrawdownThresholdFraction'
> &
  // Пороги рыночных событий (задача 49) в запросе необязательны: отсутствие поля = «не менять» (частичный PUT).
  Partial<Pick<SettingsResponse, 'spreadWideningThresholdFraction' | 'priceDrawdownThresholdFraction'>>;

/** Ответ PUT /api/settings/tinvest-token. */
export interface TInvestTokenResponse {
  tInvestTokenConfigured: boolean;
  tInvestTokenMasked: string | null;
  /** маска (последние 4 символа) Id счёта, к которому привязан провалидированный токен. */
  validatedAccountIdMasked: string | null;
}

// ---- GET /api/live/positions, GET /api/live/portfolio-intraday ----

/** Строка живых котировок одной позиции — GET /api/live/positions. */
export interface LivePositionRow {
  positionId: number;
  instrumentId: number;
  lastPriceRub: number | null;
  marketValueRub: number;
  changeDayPercent: number | null;
  /** True — тиков ещё нет, цена взята из последнего полного синка (не "живая"). */
  isStale: boolean;
  asOfUtc: string;
}

/** Ответ GET /api/live/positions. */
export interface LivePositionsResponse {
  positions: LivePositionRow[];
  /** Стоимость облигаций (грязная), без свободных денег. */
  totalMarketValueRub: number;
  asOfUtc: string;
  /** Свободные деньги на счёте по последнему синку (руб.); null — ещё не синкались. */
  cashRub?: number | null;
  /** Полная стоимость счёта по оценке брокера на момент синка (руб.) — для сверки. */
  brokerTotalRub?: number | null;
  balanceSyncedAtUtc?: string | null;
}

/** Диапазон интрадей-графика стоимости портфеля. */
export type IntradayRange = '1d' | '5d';

/** Точка интрадей-ряда суммарной стоимости портфеля. */
export interface IntradaySeriesPoint {
  tsUtc: string;
  totalMarketValueRub: number;
}

// ---- GET /api/analytics/relative-value ----

/** Насколько надёжна статистика корзины (RelativeValueService.RelativeValueConfidence):
 * High — родная корзина сектор×дюрация набрала ≥5 бумаг, Medium — откат на весь сектор,
 * Low — откат на весь рынок. */
export type RelativeValueConfidence = 'High' | 'Medium' | 'Low';

/** Cheap — спред бумаги заметно ВЫШЕ медианы её корзины («дешёвая», недооценена рынком либо
 * закладывает риск); Rich — заметно НИЖЕ («дорогая», кандидат на пересмотр); Fair — в пределах
 * порога (не заслуживает бейджа); InsufficientData — сопоставимых бумаг мало (откат на «весь рынок»,
 * Confidence Low): вердикта нет (задача 44). */
export type RelativeValueVerdict = 'Cheap' | 'Fair' | 'Rich' | 'InsufficientData';

export interface RelativeValueBasket {
  sector: string;
  durationBucket: string;
  count: number;
  confidence: RelativeValueConfidence;
}

/** Дешёвый кандидат из ТОЙ ЖЕ корзины, что Rich-позиция (топ-3). */
export interface RelativeValueCandidate {
  secid: string;
  name: string | null;
  yieldFraction: number | null;
  /** ДОЛЯ — положительное значение (кандидат дешевле медианы корзины). */
  deviationFraction: number;
  liquidityScore: LiquidityScore;
}

export interface RelativeValuePosition {
  positionId: number;
  basket: RelativeValueBasket;
  /** ДОЛЯ (0.002 = 20 б.п.) — рисуется фронтом через formatBp. */
  deviationFraction: number;
  /** ДОЛЯ (0.002 = 20 б.п.) — порог вердикта корзины: max(20 б.п., 0.25 × IQR корзины) (задача 44). */
  thresholdFraction: number;
  /** Перцентиль внутри эффективной корзины, 0-100. */
  percentile: number;
  verdict: RelativeValueVerdict;
  /** Сколько торговых дней истории легло в основу сглаженной медианы (0 — молодой банк, единственный снимок). */
  basedOnDays: number;
  /** Только для Rich-позиций — иначе пусто. */
  cheapCandidates: RelativeValueCandidate[];
}

/** Ответ GET /api/analytics/relative-value. */
export interface RelativeValueResponse {
  positions: RelativeValuePosition[];
  disclaimer: string;
}

/** Ответ GET /api/live/portfolio-intraday. */
export interface PortfolioIntradayResponse {
  points: IntradaySeriesPoint[];
}

// ---- Скоркарта надёжности: полная карточка и ручной ввод данных эмитента (задача 47) ----

/** Ответ GET /api/universe/{secid|isin}/reliability — полная карточка скоркарты (зеркалит `ReliabilityResponseDto`). */
export interface ReliabilityResponse {
  secid: string;
  isin: string | null;
  name: string | null;
  /** ИНН эмитента (строка, ведущие нули значимы) — для форм ручного ввода; null — эмитент ещё не определён. */
  emitentInn: string | null;
  emitentTitle: string | null;
  riskSignals: RiskSignals;
  disclaimer: string;
}

/** Один введённый вручную рейтинг агентства (зеркалит `IssuerRatingDto`). */
export interface IssuerRating {
  agency: string;
  rating: string;
  /** ISO-дата "2026-03-01". */
  ratingDate: string;
  note: string | null;
  /** `true` — старше срока годности (`freshMonths`): в скоркарте считается «нет данных». */
  isStale: boolean;
}

/** Ответ GET /api/issuers/{inn}/ratings. */
export interface IssuerRatingsResponse {
  inn: string;
  ratings: IssuerRating[];
  /** Срок годности рейтинга, месяцы. */
  freshMonths: number;
  disclaimer: string;
}

/** Тело POST /api/issuers/{inn}/ratings (upsert по (inn, agency)). */
export interface IssuerRatingRequest {
  agency: string;
  rating: string;
  /** ISO-дата, не в будущем. */
  ratingDate: string;
  note?: string | null;
}

/** Один отчётный год эмитента — все суммы РУБЛИ (не тысячи). `source`: "girbo" | "manual". */
export interface IssuerFinancialsYear {
  period: number;
  source: string;
  revenue: number | null;
  operatingProfit: number | null;
  interestPaid: number | null;
  netProfit: number | null;
  equity: number | null;
  totalAssets: number | null;
  currentAssets: number | null;
  currentLiabilities: number | null;
  cash: number | null;
  longDebt: number | null;
  shortDebt: number | null;
  retainedEarnings: number | null;
  hasAudit: boolean | null;
}

/** Ответ GET /api/issuers/{inn}/financials. */
export interface IssuerFinancialsResponse {
  inn: string;
  /** Статус последней проверки ГИР БО: ok | not_found | hidden | cb | error; null — не проверялся. */
  girBoStatus: string | null;
  years: IssuerFinancialsYear[];
  disclaimer: string;
}

/** Строки отчётности, принимаемые POST /api/issuers/{inn}/financials. Все суммы — РУБЛИ; не заданное = «не заполнено». */
export type IssuerFinancialsAmountKey =
  | 'revenue'
  | 'operatingProfit'
  | 'interestPaid'
  | 'netProfit'
  | 'equity'
  | 'totalAssets'
  | 'currentAssets'
  | 'currentLiabilities'
  | 'cash'
  | 'longDebt'
  | 'shortDebt'
  | 'retainedEarnings';

/** Тело POST /api/issuers/{inn}/financials. */
export type IssuerFinancialsRequest = { period: number } & Partial<Record<IssuerFinancialsAmountKey, number>>;

// ---- GET /api/analytics/position-verdicts (задача 49) ----

/** Вердикт позиции — имя enum бэка `PositionVerdictKind`. Формулировки — «кандидат/рассмотреть», не «купите/продайте». */
export type PositionVerdictKind = 'Hold' | 'BuyMoreCandidate' | 'ConsiderReplace' | 'ConsiderExit';

/** Уверенность вердикта — имя enum бэка `VerdictConfidence` (по покрытию данными скоркарты). */
export type VerdictConfidence = 'Low' | 'Medium' | 'High';

/** Лучшая замена, вынесшая вердикт ConsiderReplace. Выгода — ДОЛЯ годовых (0.008 = 80 б.п.), рубли — за эффективный горизонт, горизонт — ГОДЫ. */
export interface BestReplacement {
  name: string;
  secid: string | null;
  isin: string | null;
  /** Светофор цели: 'Green' | 'Yellow' | 'Red'. */
  targetReliability: string;
  annualizedBenefitFraction: number | null;
  /** null — налог продажи оценить нельзя (журнал неполон): в выгоду он не вошёл. */
  netBenefitAfterTaxRub: number | null;
  effectiveHorizonYears: number;
}

/** Вердикт одной позиции (бэкенд `PositionVerdictDto`). `reasons` — до 3 причин словами, главная первой. */
export interface PositionVerdict {
  positionId: number;
  instrumentId: number;
  verdict: PositionVerdictKind;
  reasons: string[];
  confidence: VerdictConfidence;
  bestReplacement: BestReplacement | null;
}

export interface PositionVerdictsResponse {
  verdicts: PositionVerdict[];
  disclaimer: string;
}

// ─── Фонды (БПИФ), задача 53 ───────────────────────────────────────────────────────────────────────────────
// Зеркало FundsDtos.cs. Деньги — РУБЛИ (цена — за пай), доходности и доли — ДОЛИ (0.12 = 12%); ×100 только в форматтерах.

/** Способ прогноза доходности фонда (enum бэкенда, camelCase). */
export type FundForecastMethod = 'auto' | 'distributions' | 'indexYield' | 'trailingReturn';

export type FundOutlookConfidence = 'low' | 'medium' | 'high';

export type FundOutlookWarning = 'terUnknown' | 'indexStale' | 'fewDistributions' | 'shortHistory';

export type FundOperationType = 'buy' | 'sell' | 'distribution' | 'tax' | 'fee' | 'securityIn' | 'securityOut';

export interface FundWindowReturn {
  /** Доходность за окно, ДОЛЯ. */
  returnFraction: number;
  /** Пересчёт в годовую, ДОЛЯ годовых. */
  annualizedFraction: number;
  days: number;
}

export interface FundPerformance {
  m1: FundWindowReturn | null;
  m3: FundWindowReturn | null;
  m6: FundWindowReturn | null;
  m12: FundWindowReturn | null;
  sinceFirstBuy: FundWindowReturn | null;
}

export interface FundOutlook {
  requestedMethod: FundForecastMethod;
  /** Применённый метод (при `auto` — во что разрешился). */
  method: FundForecastMethod;
  /** ДОЛЯ годовых; null — оценка недоступна (см. `unavailableReason`). */
  expectedAnnualYieldFraction: number | null;
  /** После НДФЛ (единая ставка 13%, упрощение — ко всей доходности), ДОЛЯ. */
  afterTaxYieldFraction: number | null;
  basisLabel: string;
  expectedIncome1yRub: number | null;
  expectedMonthlyIncomeRub: number | null;
  confidence: FundOutlookConfidence;
  asOf: string;
  warnings: FundOutlookWarning[];
  unavailableReason: string | null;
}

export interface FundRow {
  fundId: number;
  ticker: string | null;
  name: string | null;
  managementCompany: string | null;
  quantity: number;
  /** Рубли за пай. */
  currentPriceRub: number | null;
  priceAsOf: string | null;
  marketValueRub: number;
  /** Средняя цена остатка за пай (average cost), рубли. */
  avgCostRub: number;
  /** Себестоимость остатка, рубли. */
  investedRub: number;
  unrealizedPnlRub: number | null;
  /** ДОЛЯ от себестоимости остатка. */
  unrealizedPnlFraction: number | null;
  distributionsGrossRub: number;
  /** Со знаком брокера (удержание −). */
  taxesRub: number;
  distributionsNetRub: number;
  totalResultRub: number;
  totalReturnFraction: number | null;
  xirrFraction: number | null;
  /** TER, ДОЛЯ годовых. */
  totalExpenseFraction: number | null;
  performance: FundPerformance;
  outlook: FundOutlook;
  /** Доля в (облигации + фонды), ДОЛЯ. */
  shareOfPortfolioFraction: number | null;
  hasUnknownLots: boolean;
  dataIncomplete: boolean;
}

export interface FundsTotals {
  marketValueRub: number;
  investedRub: number;
  unrealizedPnlRub: number;
  incomeNetRub: number;
  totalResultRub: number;
}

/** Ответ GET /api/funds. */
export interface FundsResponse {
  asOf: string;
  funds: FundRow[];
  totals: FundsTotals;
  disclaimer: string;
}

export interface FundPricePoint {
  date: string;
  closeRub: number;
}

export interface FundDistribution {
  recordDate: string;
  paymentDate: string | null;
  amountPerShareRub: number;
  /** Выплата / цена пая на дату фиксации, ДОЛЯ за период (не годовая). */
  yieldOnPriceFraction: number | null;
}

export interface FundOperation {
  date: string;
  type: FundOperationType;
  amountRub: number;
  quantity: number | null;
}

export interface FundSettings {
  forecastMethod: FundForecastMethod;
  benchmarkIndex: string | null;
}

/** Ответ GET /api/funds/{id}. */
export interface FundDetail {
  asOf: string;
  fund: FundRow;
  priceHistory: FundPricePoint[];
  distributions: FundDistribution[];
  operations: FundOperation[];
  settings: FundSettings;
  disclaimer: string;
}

/** Ответ GET /api/portfolio/summary. */
export interface PortfolioSummary {
  bondsValueRub: number;
  fundsValueRub: number;
  cashRub: number | null;
  totalRub: number;
  brokerTotalRub: number | null;
  /** brokerTotal − total; null — брокерского итога нет. */
  brokerDiffRub: number | null;
  balanceSyncedAt: string | null;
  /** XIRR всего портфеля (облигации + фонды), ДОЛЯ годовых. */
  portfolioXirrFraction: number | null;
  disclaimer: string;
}

// ── Доходы (задача 54; контракт — Endpoints/Dtos/IncomeDtos.cs). Деньги — РУБЛИ, доли — ДОЛИ (0.12 = 12%). ──

export type IncomeAssetKind = 'bond' | 'fund';
export type IncomeAssetFilter = 'all' | IncomeAssetKind;
/** open — позиция есть; closed — продана; redeemed — погашена. */
export type IncomeStatus = 'open' | 'closed' | 'redeemed';
export type IncomeLedgerKind =
  | 'buy'
  | 'sell'
  | 'income'
  | 'tax'
  | 'fee'
  | 'principalReturn'
  | 'redemption'
  | 'securityIn'
  | 'securityOut';

export interface IncomeTotals {
  positionsCount: number;
  investedTotalRub: number;
  marketValueCleanRub: number;
  accruedNowRub: number;
  unrealizedPnlRub: number;
  realizedPnlRub: number;
  incomeGrossRub: number;
  accruedPaidRub: number;
  accruedReceivedRub: number;
  /** брутто − НКД уплаченный + НКД полученный + накопленный НКД на сегодня (БЕЗ налогов). */
  netCouponIncomeRub: number;
  /** Налоги по позициям, знак брокера (обычно −); «по счёту» — в `accountLevel`. */
  taxesRub: number;
  feesRub: number;
  principalReturnedRub: number;
  /** Итог уже включает налоги/комиссии «по счёту». */
  totalResultRub: number;
  totalReturnFraction: number | null;
}

export interface IncomeAccountLevel {
  taxesRub: number;
  feesRub: number;
}

export interface IncomePeriod {
  couponsGrossRub: number;
  distributionsGrossRub: number;
  accruedPaidRub: number;
  accruedReceivedRub: number;
  taxesRub: number;
  feesRub: number;
  realizedPnlRub: number;
  netRub: number;
}

export interface IncomeMonth {
  /** "yyyy-MM". */
  month: string;
  couponsGrossRub: number;
  distributionsGrossRub: number;
  /** −уплачен + получен. */
  accruedNetRub: number;
  taxesRub: number;
  feesRub: number;
  realizedPnlRub: number;
  netRub: number;
}

export interface IncomePosition {
  assetKind: IncomeAssetKind;
  assetId: number;
  name: string | null;
  ticker: string | null;
  isin: string | null;
  /** Только у открытых облигаций (ссылка на карточку). */
  positionId: number | null;
  fundId: number | null;
  dataIncomplete: boolean;
  status: IncomeStatus;
  firstDate: string;
  lastDate: string;
  holdingDays: number;
  quantityNow: number;
  investedTotalRub: number;
  costRemainingRub: number;
  marketValueCleanRub: number;
  accruedNowRub: number;
  /** null у закрытых. */
  unrealizedPnlRub: number | null;
  realizedPnlRub: number;
  incomeGrossRub: number;
  accruedPaidRub: number;
  accruedReceivedRub: number;
  netCouponIncomeRub: number;
  taxesRub: number;
  feesRub: number;
  principalReturnedRub: number;
  totalResultRub: number;
  totalReturnFraction: number | null;
  xirrFraction: number | null;
  hasUnknownLots: boolean;
  accruedUnknown: boolean;
  periodIncomeGrossRub: number;
  periodRealizedPnlRub: number;
  periodTaxesRub: number;
  periodFeesRub: number;
}

/** Ответ GET /api/income. */
export interface IncomeResponse {
  asOf: string;
  from: string | null;
  to: string | null;
  assetKind: IncomeAssetFilter;
  totals: IncomeTotals;
  accountLevel: IncomeAccountLevel;
  period: IncomePeriod;
  monthly: IncomeMonth[];
  positions: IncomePosition[];
  portfolioXirrFraction: number | null;
  unrealizedIsAllTime: boolean;
  disclaimer: string;
}

export interface IncomeLedgerEntry {
  /** ISO date-time. */
  date: string;
  kind: IncomeLedgerKind;
  /** Знак брокера: покупка −, поступление +. */
  amountRub: number;
  quantity: number | null;
  /** НКД операции на всю операцию, ≥ 0; null — неизвестен. */
  accruedRub: number | null;
  quantityAfter: number;
  avgCostAfterRub: number;
  realizedPnlRub: number;
}

/** Ответ GET /api/income/{bond|fund}/{assetId}/ledger. */
export interface IncomeLedgerResponse {
  assetKind: IncomeAssetKind;
  assetId: number;
  name: string | null;
  ticker: string | null;
  isin: string | null;
  status: IncomeStatus;
  disclaimer: string;
  entries: IncomeLedgerEntry[];
}

export interface IncomeQuery {
  from?: string;
  to?: string;
  assetKind: IncomeAssetFilter;
  includeClosed: boolean;
}
