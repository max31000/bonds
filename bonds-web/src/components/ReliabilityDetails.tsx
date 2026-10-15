import { Center, Loader, Stack, Text } from '@mantine/core';
import { ErrorState } from '@mvv42/kit';
import { useReliability } from '../hooks/useReliability';
import type { RiskSignals } from '../api/types';
import { ReliabilityScoreCard } from './ReliabilityScoreCard';

/**
 * Содержимое попапа/ящика светофора (задача 47): `ReliabilityScoreCard` с полной разбивкой. Если компоненты уже
 * есть у родителя (кандидаты-замены, аллокация, сравнивалка получают `RiskSignals` целиком) — рисуем сразу; строки
 * скринера несут только свёртку (score/coverage/флаги), поэтому полная карточка запрашивается лениво — только при
 * раскрытии (`lookup` = secid/ISIN, GET /api/universe/{secid}/reliability).
 */
export function ReliabilityDetails({
  signals,
  lookup,
  testIdSuffix,
}: {
  signals?: RiskSignals;
  lookup?: string | null;
  testIdSuffix: string;
}) {
  const hasComponents = (signals?.components?.length ?? 0) > 0;
  const { data, isLoading, error } = useReliability(hasComponents ? null : (lookup ?? null));

  if (hasComponents && signals) {
    return <ReliabilityScoreCard riskSignals={signals} testIdSuffix={testIdSuffix} />;
  }

  if (isLoading) {
    return (
      <Center py="md" data-testid={`reliability-details-loading-${testIdSuffix}`}>
        <Loader size="sm" />
      </Center>
    );
  }

  if (error || !data) {
    return (
      <Stack gap="xs">
        <ErrorState compact title="Скоркарта недоступна" error={error ?? 'Нет данных'} data-testid={`reliability-details-error-${testIdSuffix}`} />
        {signals && (
          <Text size="xs" c="dimmed">
            {signals.reliabilityReason}
          </Text>
        )}
      </Stack>
    );
  }

  return <ReliabilityScoreCard riskSignals={data.riskSignals} testIdSuffix={testIdSuffix} />;
}
