import { Paper, Text } from '@mantine/core';

interface DisclaimerProps {
  /** Текст дисклеймера от backend (поле `disclaimer` в ответах аналитических эндпоинтов). */
  text?: string;
}

const DEFAULT_TEXT =
  'Все расчёты в этом сервисе — аналитические оценки на основе доступных рыночных данных, ' +
  'а не индивидуальная инвестиционная рекомендация. Решения об операциях с ценными бумагами ' +
  'принимайте самостоятельно, с учётом собственной оценки рисков.';

/**
 * Переиспользуемый дисклеймер (см. спека §6/§11). Показывается на всех аналитических
 * экранах (позиции — 09a; календарь/аналитика/сигналы — 09b/09c).
 */
export function Disclaimer({ text }: DisclaimerProps) {
  return (
    <Paper withBorder p="sm" radius="md" data-testid="disclaimer">
      <Text size="xs" c="dimmed">
        {text?.trim() || DEFAULT_TEXT}
      </Text>
    </Paper>
  );
}
