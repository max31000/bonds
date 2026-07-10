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
  /** Задача 24: НКД на одну бумагу — уже включён в marketValueRub, показывается как разложение. */
  accruedPerBondRub: number;
  /** Задача 24: НКД на всю позицию = accruedPerBondRub × quantity. */
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

  // ---- Цена входа / P&L "от цены входа" (plan/14) — null, если по журналу операций не посчитать. ----

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

// ---- GET /api/positions/{id} (см. plan/19 §A) — карточка позиции/drill-down ----

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
  /** Задача 24: marketValueRub − accruedTotalRub — «чистая» стоимость остатка без НКД. */
  cleanValueRub: number;
  /** Задача 24: НКД на всю позицию, уже включённый в marketValueRub (разложение). */
  accruedTotalRub: number;
  commissionRub: number;
  commissionRate: number;
  /** Plan/22 часть E: источник commissionRate (override/оценка из журнала/дефолт). */
  commissionRateSource: CommissionRateSource;
  netProceedsRub: number;
  realizedPnlRub: number | null;
  realizedPnlPercent: number | null;
  couponsReceivedRub: number | null;
  totalReturnWithCouponsRub: number | null;
  pnlAvailable: boolean;
  /** Задача 25: оценка НДФЛ с продажи (13% с прибыли к средней цене входа) — null, если журнал операций неполон/cost basis недоступен. */
  taxEstimateRub: number | null;
  /** Задача 25: totalReturnWithCouponsRub − taxEstimateRub — итог после налога. Null, если taxEstimateRub недоступен. */
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
  /** Задача 24: НКД на всю позицию = accruedInterest × quantity. */
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

// ---- GET /api/cashflow (см. plan/09b §B.2) ----

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

// ---- GET /api/analytics/scatter (см. plan/09b §B.3) ----

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
  /** T-7/L-1: дюрация Маколея — по ней строится ось X scatter (согласована с G-спредом). */
  macaulayDuration: number;
  effectiveYield: number;
  yieldKind: YieldKind;
  isFloater: boolean;
  isIndexed: boolean;
  isEstimated: boolean;
  dataIncomplete: boolean;
  /** Задача 20: true — точка watchlist-бумаги без позиции (полый маркер на графике). */
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

// ---- GET /api/analytics/composition (см. plan/09b §B.4) ----

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

// ---- GET /api/analytics/xirr (см. plan/09b §B.5) ----

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

/** Ответ POST /api/analytics/xirr/backfill (plan/15 §B.4) — сколько новых точек истории записано. */
export interface XirrBackfillResponse {
  pointsWritten: number;
}

// ---- GET /api/signals, POST /api/signals/{id}/read (см. plan/09c §B.6) ----

/** Уровень важности сигнала. */
export type SignalSeverity = 'Low' | 'Medium' | 'High';

/** Сигнал по позиции/портфелю (купон, оферта, концентрация и т.д., см. спека §8). */
export interface Signal {
  id: number;
  type: string;
  severity: SignalSeverity;
  positionId: number | null;
  instrumentId: number | null;
  suggestedAction: string;
  date: string;
  isRead: boolean;
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

// ---- POST /api/sync, GET /api/sync/status (см. plan/09c §B.7) ----

/** Ответ POST /api/sync — результат единичного цикла форс-синхронизации. */
export interface SyncRunResult {
  alreadyRunning: boolean;
  noAccountConfigured: boolean;
  /** T-13/B: токен T-Invest не задан или не расшифровался — синк деградировал на этом шаге. */
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
  /** T-13/B: токен T-Invest не задан или не расшифровался (см. SyncRunResult). */
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

// ---- GET /api/analytics/comparison, POST /api/analytics/replacement (plan/17 §A) ----

/** Строка таблицы сравнения позиций — GET /api/analytics/comparison. */
export interface ComparisonRow {
  positionId: number;
  instrumentId: number;
  name: string | null;
  issuer: string | null;
  effectiveYield: number | null;
  yieldKind: YieldKind;
  modifiedDuration: number | null;
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
  /** Target — позиция портфеля. Игнорируется на бэке, если задан targetInstrumentId (задача 20). */
  targetPositionId?: number;
  /** Задача 20: target — watchlist-бумага без позиции, по InstrumentId. */
  targetInstrumentId?: number;
  horizonYears: number;
  sellCommissionRate?: number;
  buyCommissionRate?: number;
}

/** Ответ POST /api/analytics/replacement — «держать A vs переложиться в B» (SwitchAnalysisService). */
export interface ReplacementResponse {
  holdPositionId: number;
  targetPositionId: number;
  /** Задача 20: эхо запроса — задан, только если target был watchlist-бумагой без позиции. */
  targetInstrumentId?: number | null;
  horizonYears: number;
  sellCommissionRub: number;
  buyCommissionRub: number;
  totalSwitchCostRub: number;
  netBenefitRub: number;
  isSwitchFavorable: boolean;
  breakEvenYears: number | null;
  yieldDataIncomplete: boolean;
  disclaimer: string;

  /** Plan/22 часть E: фактически применённая ставка продажи/покупки — ДОЛЯ. */
  sellCommissionRateUsed: number;
  buyCommissionRateUsed: number;
  /** Plan/22 часть E: источник ставки — CommissionRateSource либо "ExplicitRequest" (обе ставки заданы явно в запросе). */
  commissionRateSource: CommissionRateSource | 'ExplicitRequest';

  /** Задача 27 часть B: та же формула-разбивка, что MatrixPair — спред эффективных доходностей (targetYield − holdYield), ДОЛЯ. Null — yieldDataIncomplete. */
  spreadFraction: number | null;
  /** Задача 27 часть B: капитал, реально переходящий в target (marketValueRub hold минус комиссия продажи), в рублях. */
  capitalRub: number;
  /** Задача 27 часть B: валовая выгода (спред × капитал × горизонт) ДО вычета комиссий, в рублях. Null — yieldDataIncomplete. */
  grossGainRub: number | null;
  /** Задача 27 часть B: netBenefitRub / capitalRub / horizonYears — ДОЛЯ (не процент). */
  annualizedBenefitFraction: number | null;
  /** Задача 27 часть B: оценка НДФЛ от продажи hold-позиции — null, если cost basis недоступен/журнал неполон. */
  sellTaxEstimateRub: number | null;
  /** Задача 27 часть B: netBenefitRub − sellTaxEstimateRub. Null, если sellTaxEstimateRub недоступен. */
  netBenefitAfterTaxRub: number | null;
  /** Задача 33 часть A.4/35: риск-сигналы таргета (см. <see>RiskSignals</see>) — только если таргет
   * найден в банке (bond_universe) по ISIN, иначе null (бумага вне биржевой статистики MOEX). */
  targetRiskSignals?: RiskSignals | null;
}

// ---- GET /api/analytics/replacement-candidates (задача 33 часть B, фронт — задача 35 §A) ----

/** Уровень информационного риск-сигнала кандидата — НЕ рейтинг кредитного качества/агентства. */
export type RiskSignalLevel = 'Good' | 'Neutral' | 'Caution';

/**
 * Задача 38 часть A — светофор надёжности: ОДИН агрегат поверх двух риск-сигналов (см.
 * `RiskSignals`) + листинга + сектора. Зеркалит backend `ReliabilityLight`. <b>НЕ кредитный
 * рейтинг</b> — сигнал по биржевой статистике MOEX, см. `RiskSignalsCaption`/тултип
 * `RiskSignalBadges` для обязательного дисклеймера.
 */
export type ReliabilityLevel = 'Green' | 'Yellow' | 'Red';

/**
 * Два ИНФОРМАЦИОННЫХ риск-сигнала кандидата-замены (ликвидность+листинг, отклонение спреда от
 * медианы его корзины) — зеркалит `RiskSignalsDto` (задача 33 часть A). Не ранжирует кандидатов
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
  /** Задача 38 часть A.2: светофор надёжности — см. `ReliabilityLevel`. */
  reliability: ReliabilityLevel;
  /** Человекочитаемое обоснование `reliability` — задача 38 часть A.2. */
  reliabilityReason: string;
}

/** Режим подбора кандидатов-замен для одной позиции портфеля (GET /replacement-candidates). */
export type ReplacementCandidatesMode = 'market' | 'rv';

/** Один кандидат-замена — дешёвая банк-статистика (bond_universe), НЕ точный расчёт движка (см.
 * doc-comment backend `ReplacementCandidateDto`) — точную выгоду считает POST /replacement. */
export interface ReplacementCandidate {
  secid: string;
  isin: string | null;
  name: string;
  /** Задача 33 часть B.2: банк-слой не хранит эмитента — всегда null. */
  issuer: string | null;
  sector: string | null;
  /** ДОЛЯ. */
  yieldFraction: number | null;
  /** Годы. */
  durationYears: number | null;
  /** ДОЛЯ; приближённый G-спред банка. */
  gSpreadFraction: number | null;
  /** Задача 37 часть A: дата оферты (ISO "2028-01-01"), если есть — доходность к оферте считается
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
}

// ---- GET /api/analytics/allocation (plan/17 §B, задача 34 — source=market/recommended) ----

/** Причина, по которой кандидат не получил докупку. Зеркалит CashAllocationSkipReason бэкенда. */
export type AllocationSkipReason = 'NoYield' | 'ConcentrationLimit' | 'NoPrice' | 'NotComparable' | 'InsufficientFunds';

/** Источник кандидатов аллокации (задача 34) — эхо запроса/выбор пользователя. */
export type AllocationSource = 'portfolio' | 'market' | 'recommended';

/** Одна строка распределения — сколько купить конкретной бумаги. */
export interface AllocationLine {
  /** Задача 34: null для Source=market/recommended (банк-кандидат не связан с Instrument) —
   * идентификатор таких строк — {@link AllocationLine.secid}. */
  instrumentId: number | null;
  /** Задача 34: биржевой SECID — только для Source=market/recommended. Null для Source=portfolio. */
  secid: string | null;
  name: string | null;
  issuer: string | null;
  /** Задача 34: грубая секторная классификация банка — заполнена для Source=market/recommended. */
  sector: string | null;
  quantity: number;
  estimatedCostRub: number;
  effectiveYield: number;
  lotSizeAssumed: boolean;
  /** Задача 24: разложение estimatedCostRub — чистая цена всей докупки (без НКД/комиссии). cleanCostRub + accruedCostRub + commissionCostRub = estimatedCostRub. */
  cleanCostRub: number;
  /** Задача 24: НКД в составе estimatedCostRub. */
  accruedCostRub: number;
  /** Задача 24: комиссия покупки в составе estimatedCostRub. */
  commissionCostRub: number;
  /** Задача 34 часть A.2: риск-сигналы — заполнены для Source=market/recommended, null для Source=portfolio. */
  riskSignals: RiskSignals | null;
}

/** Кандидат, не получивший докупку, и причина. */
export interface AllocationSkip {
  /** Null для Source=market/recommended — см. doc-comment {@link AllocationLine.instrumentId}. */
  instrumentId: number | null;
  /** Задача 34: см. {@link AllocationLine.secid}. */
  secid: string | null;
  name: string | null;
  issuer: string | null;
  reason: AllocationSkipReason;
}

/** Ответ GET /api/analytics/allocation. */
export interface AllocationResponse {
  amountRub: number;
  /** Задача 34: эхо запрошенного источника кандидатов. */
  source: string;
  allocations: AllocationLine[];
  skipped: AllocationSkip[];
  leftoverRub: number;
  disclaimer: string;

  /** Plan/22 часть E: ставка комиссии покупки, применённая к цене лота — ДОЛЯ. */
  commissionRateUsed: number;
  commissionRateSource: CommissionRateSource;

  /** Задача 34 часть B.4: только для Source=market/recommended — сколько кандидатов реально
   * подходило до отсечения потолком. Null для Source=portfolio. */
  candidatePoolAvailable: number | null;
  /** Задача 34 часть B.4: верхняя граница числа кандидатов, участвовавших в жадном проходе. Null для Source=portfolio. */
  candidatePoolLimit: number | null;
  /** Задача 34 часть B.4: true — потолок реально обрезал часть подходящих кандидатов. Null для Source=portfolio. */
  candidatePoolTruncated: boolean | null;
}

// ---- POST /api/analytics/basket (plan/29 §B) ----

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

// ---- GET/POST /api/watchlist, DELETE /api/watchlist/{id} (plan/20 §A) ----

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

// ---- GET/PUT /api/settings, PUT /api/settings/tinvest-token (см. plan/09c §B.8) ----

/** Read-only авто-оценка ставки комиссии из журнала операций (plan/22 часть A/D). Null — журнал не позволяет оценить (нет сделок/оборот 0). */
export interface CommissionAutoEstimate {
  /** Оценённая ставка — ДОЛЯ (0.00046 = 0.046%), не процент. */
  rate: number;
  turnoverRub: number;
  feeTotalRub: number;
  tradeCount: number;
  windowMonths: number;
}

/** Источник эффективной ставки комиссии (plan/22 часть C). */
export type CommissionRateSource = 'UserOverride' | 'EstimatedFromTrades' | 'Default';

/** Настройки пользователя — пороги триггеров сигналов и базовая валюта. */
export interface SettingsResponse {
  baseCurrency: 'RUB';
  tInvestTokenConfigured: boolean;
  tInvestTokenMasked: string | null;
  upcomingEventDaysThreshold: number;
  uninvestedCashThresholdRub: number;
  uninvestedCashLookbackDays: number;
  yieldBelowAlternativeBpsThreshold: number;
  maturityWindowDaysForAlternativeComparison: number;
  defaultMaxConcentrationPercent: number;
  durationDriftToleranceYears: number;

  /** Plan/22 часть D: ручной override ставки комиссии — ДОЛЯ (0.0005 = 0.05%). Null — не задан. UI вводит в %, конвертация на границе API. */
  commissionRateOverride: number | null;
  /** Read-only: авто-оценка ставки из журнала операций (часть A). Null — журнал не позволяет оценить. */
  commissionAutoEstimate: CommissionAutoEstimate | null;
  /** Read-only: имя тарифа T-Invest (GetInfo) — только контекст, не участвует в расчётах. Null — не удалось получить. */
  tInvestTariff: string | null;
  /** Read-only: ставка, которая реально применяется расчётами (резолвер части C) — ДОЛЯ. */
  commissionEffectiveRate: number;
  /** Read-only: источник commissionEffectiveRate. */
  commissionEffectiveSource: CommissionRateSource;
}

// ---- GET /api/universe, GET /api/universe/status, POST /api/universe/{secid}/materialize (plan/26, plan/27) ----

/** Скор ликвидности бумаги банка (см. LiquidityScoreCalculator) — приблизительная оценка по обороту/спреду/числу
 *  сделок. Зеркалит бэкенд-enum: «нет данных» сериализуется как 'None' (не 'Unknown'). */
export type LiquidityScore = 'High' | 'Medium' | 'Low' | 'None';

/**
 * Причина, по которой гигиенический фильтр скрыл бумагу из выдачи по умолчанию (plan/26 часть
 * D) — значения зеркалят backend enum `HygieneHiddenReason` (см.
 * `src/Bonds.Core/Universe/UniverseHygieneFilter.cs`), сериализуемый как строка (`.ToString()`).
 * Задача 28: исправлены на фактические значения enum — было `MissingYield` (никогда не
 * отправляется backend'ом), не хватало `ListLevelThree`/`ImplausibleYield`.
 */
export type UniverseHiddenReason =
  | 'LowTurnover'
  | 'ListLevelThree'
  | 'ImplausibleYield'
  | 'MissingDurationOrPrice'
  | 'NearMaturity';

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
  gspreadApproxFraction: number | null;
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
  /** Задача 38 часть B.2: светофор надёжности банк-записи — см. `ReliabilityLevel`. */
  reliability: ReliabilityLevel;
  /** Человекочитаемое обоснование `reliability`. */
  reliabilityReason: string;
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
  /** Задача 38 часть B.2: «не хуже уровня» — green = только Green; yellow = Green+Yellow; не задан/'red' = без фильтра. */
  reliability?: 'green' | 'yellow' | 'red';
  sortBy?: 'yield' | 'duration' | 'turnover' | 'gspread';
  sortDir?: 'asc' | 'desc';
  limit?: number;
  offset?: number;
}

/** Ответ GET /api/universe/status (задача 28) — сводка по банку облигаций для статусной строки скринера. */
export interface UniverseStatus {
  lastRefreshUtc: string | null;
  totalBonds: number;
  hiddenBonds: number;
  historyDays: number;
}

/** Полные метрики движка для материализованной бумаги — POST /api/universe/{secid}/materialize (задача 27). Те же поля, что WatchlistItem (тот же расчётный путь). */
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
>;

/** Ответ PUT /api/settings/tinvest-token. */
export interface TInvestTokenResponse {
  tInvestTokenConfigured: boolean;
  tInvestTokenMasked: string | null;
  /** T-13/C: маска (последние 4 символа) Id счёта, к которому привязан провалидированный токен. */
  validatedAccountIdMasked: string | null;
}

// ---- GET /api/live/positions, GET /api/live/portfolio-intraday (plan/16 часть A) ----

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
  totalMarketValueRub: number;
  asOfUtc: string;
}

/** Диапазон интрадей-графика стоимости портфеля. */
export type IntradayRange = '1d' | '5d';

/** Точка интрадей-ряда суммарной стоимости портфеля. */
export interface IntradaySeriesPoint {
  tsUtc: string;
  totalMarketValueRub: number;
}

// ---- GET /api/analytics/relative-value (задача 30) ----

/** Насколько надёжна статистика корзины (RelativeValueService.RelativeValueConfidence):
 * High — родная корзина сектор×дюрация набрала ≥5 бумаг, Medium — откат на весь сектор,
 * Low — откат на весь рынок. */
export type RelativeValueConfidence = 'High' | 'Medium' | 'Low';

/** Cheap — спред бумаги заметно ВЫШЕ медианы её корзины («дешёвая», недооценена рынком либо
 * закладывает риск); Rich — заметно НИЖЕ («дорогая», кандидат на пересмотр); Fair — в пределах
 * порога (не заслуживает бейджа). */
export type RelativeValueVerdict = 'Cheap' | 'Fair' | 'Rich';

export interface RelativeValueBasket {
  sector: string;
  durationBucket: string;
  count: number;
  confidence: RelativeValueConfidence;
}

/** Дешёвый кандидат из ТОЙ ЖЕ корзины, что Rich-позиция (топ-3, задача 30 часть C.2). */
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
