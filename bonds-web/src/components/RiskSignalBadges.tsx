import { Badge, Group, Text, Tooltip } from '@mantine/core';
import { formatBp } from '../utils/format';
import type { RiskSignals, RiskSignalLevel } from '../api/types';

const LEVEL_COLOR: Record<RiskSignalLevel, string> = {
  Good: 'green',
  Neutral: 'gray',
  Caution: 'orange',
};

function spreadLabel(signals: RiskSignals): string {
  if (signals.spreadVsBasketMedianFraction === null) return 'спред: нет данных';
  const sign = signals.spreadVsBasketMedianFraction >= 0 ? '+' : '';
  return `спред ${sign}${formatBp(signals.spreadVsBasketMedianFraction)} к корзине`;
}

/**
 * Два ИНФОРМАЦИОННЫХ риск-бейджа кандидата-замены (задача 33/35): ликвидность+листинг, отклонение
 * спреда от медианы его корзины. Уровень (см. `RiskSignalLevel`, зеркалит backend `SignalLevel`)
 * задаёт цвет — Good зелёный / Neutral серый / Caution оранжевый. НЕ рейтинг кредитного качества/
 * агентства и не ранжирует кандидатов (ранжирование mode=market — по доходности) — см.
 * <see>RiskSignalsCaption</see> для дисклеймера. Переиспользуется в блоке 1 (кандидаты-замены) и
 * блоке 2 (строки аллокации рыночных источников), план задачи 35.
 * <para><b>testIdSuffix</b> — списки кандидатов рендерят много экземпляров одновременно, суффикс
 * (обычно `secid` кандидата) держит data-testid уникальными для точечных проверок в тестах.</para>
 */
export function RiskSignalBadges({ signals, testIdSuffix }: { signals: RiskSignals; testIdSuffix?: string }) {
  const suffix = testIdSuffix ? `-${testIdSuffix}` : '';
  const spread = spreadLabel(signals);

  return (
    <Group gap={4} wrap="wrap" data-testid={`risk-signal-badges${suffix}`}>
      <Tooltip label={signals.liquidityLabel} withArrow>
        <Badge size="xs" color={LEVEL_COLOR[signals.liquidity]} variant="light" data-testid={`risk-signal-liquidity${suffix}`}>
          {signals.liquidityLabel}
        </Badge>
      </Tooltip>
      <Tooltip label={spread} withArrow>
        <Badge size="xs" color={LEVEL_COLOR[signals.spread]} variant="light" data-testid={`risk-signal-spread${suffix}`}>
          {spread}
        </Badge>
      </Tooltip>
    </Group>
  );
}

/**
 * Подпись-дисклеймер под риск-бейджами (план задачи 35 §B.2) — рендерится ОДИН раз на секцию/
 * список кандидатов, не под каждой карточкой (иначе шумно на длинных списках). Явно отделяет
 * риск-сигналы от кредитного рейтинга — формулировка владельца.
 */
export function RiskSignalsCaption() {
  return (
    <Text size="xs" c="dimmed" data-testid="risk-signals-caption">
      Риск-сигналы — по биржевой статистике MOEX (ликвидность, спред к рынку), не рейтинг рейтинговых агентств.
    </Text>
  );
}
