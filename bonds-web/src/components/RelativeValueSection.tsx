import { useEffect, useState } from 'react';
import { Paper, Title, Text, Stack, Group, UnstyledButton, Badge } from '@mantine/core';
import { useRelativeValueStore } from '../store/useRelativeValueStore';
import { ChartExplainIcon } from './charts/ChartCard';
import { MarketComparator } from './MarketComparator';
import { Disclaimer } from './Disclaimer';
import { formatBp, formatPercent } from '../utils/format';
import { liquidityLabel, liquidityColor } from '../utils/universeDisplay';
import type { RelativeValueCandidate, RelativeValuePosition } from '../api/types';

const EXPLANATION =
  'Relative Value (RV) — сравнение спреда бумаги не со всем рынком, а с медианой ЕЁ ЖЕ корзины ' +
  '(сектор × срок до погашения). «Где YTM больше» — плохой вопрос: наверху списка доходностей ' +
  'обычно преддефолтный мусор. Правильный вопрос — что дёшево ОТНОСИТЕЛЬНО СВОИХ соседей по ' +
  'корзине. Большое отклонение может означать реальный риск эмитента, а не ошибку рынка — это НЕ ' +
  'оценка кредитного качества.';

/** Один кандидат-чип (имя, YTM, +N б.п. к корзине, бейдж ликвидности) — клик прокидывает бумагу в MarketComparator. */
function CandidateChip({ candidate, onClick, isSelected }: { candidate: RelativeValueCandidate; onClick: () => void; isSelected: boolean }) {
  return (
    <UnstyledButton
      onClick={onClick}
      data-testid={`rv-candidate-chip-${candidate.secid}`}
      style={{
        border: '1px solid var(--mantine-color-gray-4)',
        borderRadius: 'var(--mantine-radius-md)',
        padding: '4px 8px',
        background: isSelected ? 'var(--mantine-color-blue-0)' : undefined,
      }}
    >
      <Group gap={6} wrap="nowrap">
        <Text size="xs" fw={600}>
          {candidate.name ?? candidate.secid}
        </Text>
        <Text size="xs" c="dimmed">
          {formatPercent(candidate.yieldFraction)}
        </Text>
        <Text size="xs" c="teal" fw={600}>
          +{formatBp(candidate.deviationFraction)}
        </Text>
        <Badge size="xs" color={liquidityColor(candidate.liquidityScore)} variant="light">
          {liquidityLabel(candidate.liquidityScore)}
        </Badge>
      </Group>
    </UnstyledButton>
  );
}

/** Строка одной Rich-позиции: её отклонение + 3 кандидата-чипа из её же корзины. */
function RichPositionRow({ position, positionLabel }: { position: RelativeValuePosition; positionLabel: string }) {
  const [selectedSecid, setSelectedSecid] = useState<string | null>(null);

  return (
    <Stack gap="xs" data-testid={`rv-rich-row-${position.positionId}`}>
      <Group justify="space-between" wrap="wrap">
        <Text size="sm" fw={600}>
          {positionLabel}
        </Text>
        <Badge size="sm" color="orange" variant="light">
          дорогая: {formatBp(position.deviationFraction)} к корзине
        </Badge>
      </Group>

      {position.cheapCandidates.length === 0 ? (
        <Text size="xs" c="dimmed">
          Дешёвых соседей по корзине не нашлось.
        </Text>
      ) : (
        <Group gap="xs" wrap="wrap">
          {position.cheapCandidates.map((candidate) => (
            <CandidateChip
              key={candidate.secid}
              candidate={candidate}
              isSelected={selectedSecid === candidate.secid}
              onClick={() => setSelectedSecid((current) => (current === candidate.secid ? null : candidate.secid))}
            />
          ))}
        </Group>
      )}

      {selectedSecid && (
        <div data-testid={`rv-market-comparator-${position.positionId}`}>
          <MarketComparator holdPositionId={position.positionId} initialSecid={selectedSecid} />
        </div>
      )}
    </Stack>
  );
}

/**
 * Задача 30 часть D.2 — секция «Дорогие бумаги — дешёвые соседи по корзине»: для каждой Rich-
 * позиции портфеля показывает её отклонение и топ-3 дешёвых кандидата из ТОЙ ЖЕ корзины (сектор ×
 * срок). Клик по кандидату открывает MarketComparator (задача 27) с предвыбранной бумагой —
 * минимальная интеграция через initialSecid, компонент задачи 27 не перестраивается.
 * <para>
 * Загружает RV-данные из useRelativeValueStore (отдельно от useRecommendationsStore) — отказ
 * GET /api/analytics/relative-value НЕ должен ронять остальную страницу рекомендаций (план часть D):
 * секция просто не рендерится (возвращает null), других секций это не касается.
 * </para>
 */
export function RelativeValueSection({ positionLabelById }: { positionLabelById: Record<number, string> }) {
  const { positionsById, disclaimer, isLoading, error, hasLoaded, load } = useRelativeValueStore();

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (isLoading || !hasLoaded) return null; // тихая загрузка — не блокирует страницу лоадером/скелетоном.
  if (error) return null; // план часть D: отказ эндпоинта не должен ронять страницу — секция просто не показывается.

  const richPositions = Object.values(positionsById).filter((p) => p.verdict === 'Rich');
  if (richPositions.length === 0) return null;

  return (
    <Paper withBorder p="md" radius="md" data-testid="relative-value-section">
      <Group gap={6} mb="sm">
        <Title order={4}>Дорогие бумаги — дешёвые соседи по корзине</Title>
        <ChartExplainIcon
          title="Relative Value"
          explanation={EXPLANATION}
          data-testid="relative-value-explain-icon"
        />
      </Group>

      <Stack gap="md">
        {richPositions.map((position) => (
          <RichPositionRow
            key={position.positionId}
            position={position}
            positionLabel={positionLabelById[position.positionId] ?? `Позиция #${position.positionId}`}
          />
        ))}
      </Stack>

      <Disclaimer text={disclaimer} />
    </Paper>
  );
}
