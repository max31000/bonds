import { IconInfoCircle } from '@tabler/icons-react';

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
    <aside className="bonds-disclaimer" data-testid="disclaimer">
      <IconInfoCircle size={14} stroke={1.8} aria-hidden="true" />
      <p>{text?.trim() || DEFAULT_TEXT}</p>
    </aside>
  );
}
