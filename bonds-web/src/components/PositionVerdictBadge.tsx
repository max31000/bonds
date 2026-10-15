import { Badge, Button, Group, List, Stack, Text, Tooltip } from '@mantine/core';
import { Section } from '@mvv42/kit';
import { Link } from 'react-router-dom';
import { formatBp, formatRub } from '../utils/format';
import type { PositionVerdict } from '../api/types';
import { CONFIDENCE_LABEL, VERDICT_META } from './positionVerdictCopy';

/** Тултип с причинами: список из ≤ 3 пунктов + напоминание, что это оценка. */
function ReasonsTooltipLabel({ verdict }: { verdict: PositionVerdict }) {
  return (
    <Stack gap={4} maw={300}>
      <Text size="xs" fw={600}>
        {VERDICT_META[verdict.verdict].label} — оценка, не рекомендация
      </Text>
      <List size="xs" spacing={2}>
        {verdict.reasons.map((reason) => (
          <List.Item key={reason}>{reason}</List.Item>
        ))}
      </List>
    </Stack>
  );
}

/**
 * Бейдж вердикта позиции (таблица/карточка на странице «Позиции»). Причины — в тултипе (≤ 3).
 * `verdict` может отсутствовать (вердикты ещё грузятся или эндпоинт отказал) — тогда ничего не рисуется.
 */
export function PositionVerdictBadge({ verdict }: { verdict: PositionVerdict | undefined }) {
  if (!verdict) return null;
  const meta = VERDICT_META[verdict.verdict];

  return (
    <Tooltip label={<ReasonsTooltipLabel verdict={verdict} />} multiline w={320} withArrow>
      <Badge
        size="sm"
        color={meta.color}
        variant="light"
        tabIndex={0}
        data-testid={`position-verdict-${verdict.positionId}`}
        data-verdict={verdict.verdict}
        style={{ cursor: 'help' }}
      >
        {meta.label}
      </Badge>
    </Tooltip>
  );
}

/**
 * Блок «Вердикт» карточки позиции: бейдж, причины списком, уверенность, лучшая замена (если она вынесла вердикт),
 * обязательный дисклеймер «оценка, не рекомендация» и ссылка «подобрать замену» (Рекомендации с выбранной позицией).
 * Единицы: `annualizedBenefitFraction` — ДОЛЯ годовых, `formatBp` умножает на 10000 один раз.
 */
export function PositionVerdictBlock({ verdict, disclaimer }: { verdict: PositionVerdict; disclaimer: string }) {
  const meta = VERDICT_META[verdict.verdict];
  const best = verdict.bestReplacement;

  return (
    <Section
      title="Вердикт"
      description="Сводка по скоркарте надёжности, относительной цене в корзине, событиям и расчёту замены"
      data-testid="position-verdict-block"
    >
      <Stack gap="sm">
        <Group gap="sm" align="center">
          <Badge size="lg" color={meta.color} variant="light" data-testid="position-verdict-block-badge" data-verdict={verdict.verdict}>
            {meta.label}
          </Badge>
          <Text size="sm" c="dimmed" data-testid="position-verdict-confidence">
            Уверенность: {CONFIDENCE_LABEL[verdict.confidence]}
          </Text>
        </Group>

        <List size="sm" spacing={4} data-testid="position-verdict-reasons">
          {verdict.reasons.map((reason) => (
            <List.Item key={reason}>{reason}</List.Item>
          ))}
        </List>

        {best && (
          <Text size="sm" data-testid="position-verdict-best-replacement">
            Лучший кандидат: <b>{best.name}</b>
            {best.annualizedBenefitFraction !== null && <> · {formatBp(best.annualizedBenefitFraction)} годовых</>}
            {best.netBenefitAfterTaxRub !== null && <> · ≈ {formatRub(best.netBenefitAfterTaxRub)} за горизонт</>}
          </Text>
        )}

        <Group>
          <Button
            component={Link}
            to={`/recommendations?positionId=${verdict.positionId}`}
            size="xs"
            variant="light"
            data-testid="position-verdict-find-replacement"
          >
            Подобрать замену
          </Button>
        </Group>

        <Text size="xs" c="dimmed" data-testid="position-verdict-disclaimer">
          Оценка, не рекомендация. {disclaimer}
        </Text>
      </Stack>
    </Section>
  );
}
