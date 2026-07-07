import { Stack, Text } from '@mantine/core';
import { formatRub, formatPercent, formatHorizon, commissionSourceLabel } from '../utils/format';
import type { CommissionRateSource } from '../api/types';

/**
 * Поля построчной формулы «держать vs переложиться», общие для карточки матрицы замен
 * (задача 23, MatrixPair) и карточки market-сравнивалки (задача 27, ReplacementResponse) — вытащено
 * в один компонент, чтобы обе карточки рендерили ОДИНАКОВУЮ формулу без копипасты (см. plan/27 §B).
 */
export interface ReplacementBreakdownData {
  spreadFraction: number | null;
  capitalRub: number;
  horizonYears: number;
  grossGainRub: number | null;
  sellCommissionRub: number;
  buyCommissionRub: number;
  netBenefitRub: number;
  annualizedBenefitFraction: number | null;
  commissionRateUsed: number;
  commissionRateSource: CommissionRateSource | 'ExplicitRequest';
  sellTaxEstimateRub: number | null;
  netBenefitAfterTaxRub: number | null;
}

/**
 * Построчная формула-разбивка: спред → капитал → горизонт → валовая выгода → минус обе комиссии →
 * чистая выгода ≈ % годовых → минус НДФЛ → выгода после налога (plan/23 §B.2, plan/25, plan/27 §B).
 * data-testid принимает суффикс, чтобы вызывающий код мог задать уникальный ключ (пара матрицы или
 * market-сравнивалка используют разные префиксы).
 */
export function ReplacementBreakdown({ data, testIdSuffix }: { data: ReplacementBreakdownData; testIdSuffix: string }) {
  return (
    <Stack gap={2} mt="xs" data-testid={`replacement-details-${testIdSuffix}`}>
      <Text size="xs">спред доходностей: {formatPercent(data.spreadFraction)}</Text>
      <Text size="xs">капитал после продажи: {formatRub(data.capitalRub)}</Text>
      <Text size="xs">горизонт: {formatHorizon(data.horizonYears)}</Text>
      <Text size="xs">валовая выгода: {formatRub(data.grossGainRub)}</Text>
      <Text size="xs">
        − комиссия продажи {formatRub(data.sellCommissionRub)} − комиссия покупки {formatRub(data.buyCommissionRub)} (
        {formatPercent(data.commissionRateUsed)}, {commissionSourceLabel(data.commissionRateSource)})
      </Text>
      <Text size="xs" fw={600}>
        = чистая выгода {formatRub(data.netBenefitRub)}
        {data.annualizedBenefitFraction !== null && <> ≈ {formatPercent(data.annualizedBenefitFraction)} годовых</>}
      </Text>
      {data.sellTaxEstimateRub !== null ? (
        <>
          <Text size="xs" data-testid={`replacement-sell-tax-${testIdSuffix}`}>
            − НДФЛ от продажи ≈ {formatRub(data.sellTaxEstimateRub)} (оценка, 13% с прибыли к средней цене входа)
          </Text>
          <Text size="xs" fw={600} data-testid={`replacement-net-after-tax-${testIdSuffix}`}>
            = выгода после налога {formatRub(data.netBenefitAfterTaxRub)}
          </Text>
        </>
      ) : (
        <Text size="xs" c="dimmed" data-testid={`replacement-tax-unavailable-${testIdSuffix}`}>
          налог не оценён: журнал операций по hold-позиции неполон
        </Text>
      )}
    </Stack>
  );
}
