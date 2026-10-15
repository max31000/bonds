import { Stack, Text } from '@mantine/core';
import { formatRub, formatPercent, formatHorizon, formatDate, commissionSourceLabel } from '../utils/format';
import type { ReplacementBreakdownData } from '../utils/replacementBreakdown';

/**
 * Построчная формула-разбивка: доходности после НДФЛ → спред → вложено → горизонт (с причиной обрезки) →
 * валовая выгода → минус комиссии → минус слиппедж → чистая выгода ≈ % годовых → минус НДФЛ продажи → выгода
 * после налога; предупреждение о смене дюрации.
 * data-testid принимает суффикс, чтобы вызывающий код мог задать уникальный ключ (карточка кандидата или
 * market-сравнивалка используют разные префиксы).
 */
export function ReplacementBreakdown({ data, testIdSuffix }: { data: ReplacementBreakdownData; testIdSuffix: string }) {
  const slippageTotal = data.sellSlippageRub + data.buySlippageRub;
  return (
    <Stack gap={2} mt="xs" data-testid={`replacement-details-${testIdSuffix}`}>
      <Text size="xs" data-testid={`replacement-yield-after-tax-${testIdSuffix}`}>
        доходность после НДФЛ {formatPercent(data.couponTaxRate)}: текущая {formatPercent(data.holdYieldAfterTaxFraction)} → цель{' '}
        {formatPercent(data.targetYieldAfterTaxFraction)}
      </Text>
      <Text size="xs">спред доходностей (до налога): {formatPercent(data.spreadFraction)}</Text>
      <Text size="xs">вложено в цель после издержек: {formatRub(data.capitalRub)}</Text>
      <Text size="xs" data-testid={`replacement-horizon-${testIdSuffix}`}>
        горизонт: {formatHorizon(data.effectiveHorizonYears)}
        {data.horizonLimitedByTarget && (
          <>
            {' '}
            (сокращён с {formatHorizon(data.horizonYears)}: {data.targetHorizonIsOffer ? 'оферта' : 'погашение'} цели
            {data.targetHorizonDate ? ` ${formatDate(data.targetHorizonDate)}` : ''})
          </>
        )}
      </Text>
      <Text size="xs">валовая выгода (разница доходностей после НДФЛ): {formatRub(data.grossGainRub)}</Text>
      <Text size="xs">
        − комиссия продажи {formatRub(data.sellCommissionRub)} − комиссия покупки {formatRub(data.buyCommissionRub)} (
        {formatPercent(data.commissionRateUsed)}, {commissionSourceLabel(data.commissionRateSource)})
      </Text>
      {data.slippageUnknown && (
        <Text size="xs" c="dimmed" data-testid={`replacement-slippage-unknown-${testIdSuffix}`}>
          спред стакана неизвестен, слиппедж{slippageTotal > 0 ? ' одной из сторон' : ''} не учтён
        </Text>
      )}
      {!(data.slippageUnknown && slippageTotal === 0) && (
        <Text size="xs" data-testid={`replacement-slippage-${testIdSuffix}`}>
          − слиппедж продажи {formatRub(data.sellSlippageRub)} − слиппедж покупки {formatRub(data.buySlippageRub)} (половина
          спреда стакана)
        </Text>
      )}
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
      {data.durationMismatch && data.durationMismatchWarning && (
        <Text size="xs" c="orange" data-testid={`replacement-duration-warning-${testIdSuffix}`}>
          {data.durationMismatchWarning}
        </Text>
      )}
    </Stack>
  );
}
