/**
 * Общие DTO-типы API. Контракты — см. plan/08 (`src/Bonds.Api/Endpoints/*.cs`).
 */

/** Тип купона облигации (Bonds.Core.Models.CouponType, как строка после сериализации). */
export type CouponType = 'Fixed' | 'Floating' | 'Indexed';

/** Строка таблицы позиций — GET /api/positions (см. `PositionsEndpoints.PositionRowDto`). */
export interface PositionRow {
  positionId: number;
  instrumentId: number;
  issuer: string | null;
  sector: string | null;
  quantity: number;
  marketValueRub: number;
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
}

/** Ответ GET /api/positions. */
export interface PositionsResponse {
  positions: PositionRow[];
  disclaimer: string;
}

// ---- GET /api/cashflow (см. plan/09b §B.2) ----

/** Тип денежного потока, освобождающего тело долга. */
export type PrincipalFlowType = 'Amortization' | 'Maturity' | 'Offer' | 'Call';

/** Агрегат денежного потока по календарному месяцу. */
export interface CashflowMonth {
  month: string;
  grossRub: number;
  taxRub: number;
  netRub: number;
  couponGrossRub: number;
  principalGrossRub: number;
  hasEstimatedFlows: boolean;
}

/** Агрегат денежного потока по позиции. */
export interface CashflowByPosition {
  positionId: number;
  instrumentId: number;
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

/** Ответ GET /api/cashflow. */
export interface CashflowResponse {
  byMonth: CashflowMonth[];
  byPosition: CashflowByPosition[];
  principalReleases: PrincipalRelease[];
  disclaimer: string;
}

// ---- GET /api/analytics/scatter (см. plan/09b §B.3) ----

/** Вид доходности, использованной для точки на scatter-графике. */
export type YieldKind = 'Ytm' | 'Current';

/** Точка позиции на графике «дюрация × доходность». */
export interface ScatterPoint {
  positionId: number;
  instrumentId: number;
  issuer: string | null;
  modifiedDuration: number;
  effectiveYield: number;
  yieldKind: YieldKind;
  isFloater: boolean;
  isIndexed: boolean;
  isEstimated: boolean;
  dataIncomplete: boolean;
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
}

// ---- GET/PUT /api/settings, PUT /api/settings/tinvest-token (см. plan/09c §B.8) ----

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
}

/** Тело PUT /api/settings — все поля кроме токена/статуса токена (read-only производные). */
export type SettingsUpdateRequest = Omit<
  SettingsResponse,
  'tInvestTokenConfigured' | 'tInvestTokenMasked'
>;

/** Ответ PUT /api/settings/tinvest-token. */
export interface TInvestTokenResponse {
  tInvestTokenConfigured: boolean;
  tInvestTokenMasked: string | null;
}
