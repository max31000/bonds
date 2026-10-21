import { apiClient } from './client';
import type { IncomeAssetKind, IncomeLedgerResponse, IncomeQuery, IncomeResponse } from './types';

/** GET /api/income — журнал доходов: итоги, помесячный ряд, позиции (включая закрытые и фонды). */
export function fetchIncome(q: IncomeQuery): Promise<IncomeResponse> {
  const params = new URLSearchParams();
  if (q.from) params.set('from', q.from);
  if (q.to) params.set('to', q.to);
  params.set('assetKind', q.assetKind);
  params.set('includeClosed', String(q.includeClosed));
  return apiClient.get<IncomeResponse>(`/income?${params.toString()}`);
}

/** GET /api/income/{bond|fund}/{assetId}/ledger — хронология операций одного актива. Для облигации assetId = instrumentId. */
export function fetchIncomeLedger(kind: IncomeAssetKind, assetId: number | string): Promise<IncomeLedgerResponse> {
  return apiClient.get<IncomeLedgerResponse>(`/income/${kind}/${assetId}/ledger`);
}
