import { apiClient } from './client';
import type { FundDetail, FundForecastMethod, FundSettings, FundsResponse, PortfolioSummary } from './types';

/** GET /api/funds — фонды (БПИФ) на счёте с P&L, доходностью по окнам и оценкой. */
export function fetchFunds(): Promise<FundsResponse> {
  return apiClient.get<FundsResponse>('/funds');
}

/** GET /api/funds/{id} — карточка фонда (в т.ч. проданного). */
export function fetchFundDetail(fundId: number | string): Promise<FundDetail> {
  return apiClient.get<FundDetail>(`/funds/${fundId}`);
}

/** PUT /api/funds/{id}/settings — метод прогноза и (опц.) код индекса. */
export function updateFundSettings(
  fundId: number | string,
  body: { forecastMethod: FundForecastMethod; benchmarkIndex?: string | null },
): Promise<FundSettings> {
  return apiClient.put<FundSettings>(`/funds/${fundId}/settings`, body);
}

/** GET /api/portfolio/summary — общий баланс: облигации + фонды + деньги, сверка с брокером. */
export function fetchPortfolioSummary(): Promise<PortfolioSummary> {
  return apiClient.get<PortfolioSummary>('/portfolio/summary');
}
