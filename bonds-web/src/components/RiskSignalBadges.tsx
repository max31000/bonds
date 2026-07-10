import { Badge, Box, Group, Stack, Text, Tooltip } from '@mantine/core';
import { formatBp } from '../utils/format';
import type { ReliabilityLevel, RiskSignals, RiskSignalLevel } from '../api/types';

const LEVEL_COLOR: Record<RiskSignalLevel, string> = {
  Good: 'green',
  Neutral: 'gray',
  Caution: 'orange',
};

/** Задача 38 часть C.1 — цвет светофора надёжности, зеркалит backend `ReliabilityLight`. */
const RELIABILITY_COLOR: Record<ReliabilityLevel, string> = {
  Green: 'green',
  Yellow: 'yellow',
  Red: 'red',
};

const RELIABILITY_ARIA_LABEL: Record<ReliabilityLevel, string> = {
  Green: 'надёжность: зелёный',
  Yellow: 'надёжность: жёлтый',
  Red: 'надёжность: красный',
};

/**
 * Задача 38 — обязательный дисклеймер светофора (владелец явно запретил формулировку «рейтинг»,
 * см. план часть C.1/рамки задачи): показывается в тултипе <see>ReliabilityDot</see> И в
 * <see>RiskSignalsCaption</see>, чтобы дисклеймер был на видном месте независимо от того, открыл
 * ли пользователь тултип точки.
 */
export const RELIABILITY_DISCLAIMER = 'Оценка по биржевой статистике, не кредитный рейтинг.';

function spreadLabel(signals: RiskSignals): string {
  if (signals.spreadVsBasketMedianFraction === null) return 'спред: нет данных';
  const sign = signals.spreadVsBasketMedianFraction >= 0 ? '+' : '';
  return `спред ${sign}${formatBp(signals.spreadVsBasketMedianFraction)} к корзине`;
}

/**
 * Задача 38 часть C.1 — светофор надёжности: цветная точка (Green зелёная / Yellow жёлтая / Red
 * красная), переиспользуется везде, где известен агрегат (<see>RiskSignalBadges</see> — кандидаты/
 * аллокация/сравнивалка, скринер — колонка «Надёжность», у которого нет полного `RiskSignals`,
 * только `reliability`/`reliabilityReason` + отдельные ликвидность/G-спред столбцы). Тултип несёт
 * `reliabilityReason` (что притянуло уровень) + <paramref name="detailLines"/> (детальные сигналы,
 * если есть) + обязательный дисклеймер <see>RELIABILITY_DISCLAIMER</see> — НЕ выдаём светофор за
 * кредитный рейтинг нигде в UI (владельческое ограничение плана задачи 38).
 */
export function ReliabilityDot({
  reliability,
  reliabilityReason,
  detailLines = [],
  testIdSuffix,
}: {
  reliability: ReliabilityLevel;
  reliabilityReason: string;
  detailLines?: string[];
  testIdSuffix: string;
}) {
  return (
    <Tooltip
      multiline
      w={280}
      withArrow
      label={
        <Stack gap={2} data-testid={`reliability-tooltip-${testIdSuffix}`}>
          <Text size="xs">{reliabilityReason}</Text>
          {detailLines.map((line) => (
            <Text size="xs" key={line}>
              {line}
            </Text>
          ))}
          <Text size="xs" fw={600}>
            {RELIABILITY_DISCLAIMER}
          </Text>
        </Stack>
      }
    >
      <Box
        component="span"
        tabIndex={0}
        role="img"
        aria-label={RELIABILITY_ARIA_LABEL[reliability]}
        data-testid={`reliability-dot-${testIdSuffix}`}
        data-reliability={reliability}
        style={{
          display: 'inline-block',
          width: 10,
          height: 10,
          borderRadius: '50%',
          backgroundColor: `var(--mantine-color-${RELIABILITY_COLOR[reliability]}-6)`,
          flexShrink: 0,
        }}
      />
    </Tooltip>
  );
}

/**
 * Два ИНФОРМАЦИОННЫХ риск-бейджа кандидата-замены (задача 33/35): ликвидность+листинг, отклонение
 * спреда от медианы его корзины. Уровень (см. `RiskSignalLevel`, зеркалит backend `SignalLevel`)
 * задаёт цвет — Good зелёный / Neutral серый / Caution оранжевый. НЕ рейтинг кредитного качества/
 * агентства и не ранжирует кандидатов (ранжирование mode=market — по доходности) — см.
 * <see>RiskSignalsCaption</see> для дисклеймера. Переиспользуется в блоке 1 (кандидаты-замены) и
 * блоке 2 (строки аллокации рыночных источников), план задачи 35.
 * <para><b>Задача 38 часть C.1</b> — светофор надёжности (<see>ReliabilityDot</see>) слева от
 * детальных бейджей: агрегирует оба сигнала + листинг/сектор в один Green/Yellow/Red индикатор,
 * детальные бейджи остаются как были (не убраны, не переставлены) — см. рамки плана задачи 38
 * («не ломать существующие два отдельных бейджа»).</para>
 * <para><b>testIdSuffix</b> — списки кандидатов рендерят много экземпляров одновременно, суффикс
 * (обычно `secid` кандидата) держит data-testid уникальными для точечных проверок в тестах.</para>
 */
export function RiskSignalBadges({ signals, testIdSuffix }: { signals: RiskSignals; testIdSuffix?: string }) {
  const suffix = testIdSuffix ? `-${testIdSuffix}` : '';
  const spread = spreadLabel(signals);

  return (
    <Group gap={4} wrap="wrap" align="center" data-testid={`risk-signal-badges${suffix}`}>
      <ReliabilityDot
        reliability={signals.reliability}
        reliabilityReason={signals.reliabilityReason}
        detailLines={[signals.liquidityLabel, spread]}
        testIdSuffix={testIdSuffix ?? 'default'}
      />
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
 * Подпись-дисклеймер под риск-бейджами (план задачи 35 §B.2, задача 38 добавляет упоминание
 * светофора) — рендерится ОДИН раз на секцию/список кандидатов, не под каждой карточкой (иначе
 * шумно на длинных списках). Явно отделяет риск-сигналы/светофор от кредитного рейтинга —
 * формулировка владельца, на видном месте (не только в тултипе точки — задача 38 требует
 * дисклеймер "на видном месте").
 */
export function RiskSignalsCaption() {
  return (
    <Text size="xs" c="dimmed" data-testid="risk-signals-caption">
      Риск-сигналы и светофор надёжности — по биржевой статистике MOEX (ликвидность, спред к
      рынку), не рейтинг рейтинговых агентств.
    </Text>
  );
}
