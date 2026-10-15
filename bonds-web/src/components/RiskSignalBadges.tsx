import { Badge, Box, Drawer, Group, Popover, Stack, Text, Tooltip } from '@mantine/core';
import { useDisclosure, useMediaQuery } from '@mantine/hooks';
import { formatBp } from '../utils/format';
import type { ReliabilityLevel, RiskSignals, RiskSignalLevel } from '../api/types';
import { ReliabilityDetails } from './ReliabilityDetails';
import { RELIABILITY_DISCLAIMER } from './reliabilityCopy';
import { RELIABILITY_ARIA_LABEL, RELIABILITY_COLOR } from './reliabilityLight';
import { ValueBadge } from './ValueBadge';

export { RELIABILITY_DISCLAIMER };

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

/** Откуда лениво взять разбивку скоркарты по клику на светофор (см. <see>ReliabilityDetails</see>). */
export interface ReliabilityDetailsSource {
  /** `RiskSignals` с `components` — разбивка рисуется сразу. */
  signals?: RiskSignals;
  /** secid/ISIN бумаги банка — карточку запросят при раскрытии, если компонентов у родителя нет (строки скринера). */
  lookup?: string | null;
}

/**
 * Светофор надёжности: цветная точка (Green зелёная / Yellow жёлтая / Red красная), переиспользуется везде, где
 * известен агрегат (<see>RiskSignalBadges</see> — кандидаты/аллокация/сравнивалка, скринер — колонка «Надёжность»,
 * у которого нет полного `RiskSignals`, только свёртка). Тултип несёт `reliabilityReason` (что притянуло уровень) +
 * «балл N из 100, подробности по клику» + <paramref name="detailLines"/> + обязательный дисклеймер
 * <see>RELIABILITY_DISCLAIMER</see> — НЕ выдаём светофор за кредитный рейтинг нигде в UI (владельческое
 * ограничение плана задачи 38).
 * <para><b>Задача 47:</b> при заданном <paramref name="details"/> точка становится кнопкой: клик/тап открывает
 * <see>ReliabilityScoreCard</see> в Popover (desktop) / Drawer (&lt; 48em), без <paramref name="details"/> — как
 * раньше, статичная точка.</para>
 */
export function ReliabilityDot({
  reliability,
  reliabilityReason,
  detailLines = [],
  testIdSuffix,
  score,
  details,
}: {
  reliability: ReliabilityLevel;
  reliabilityReason: string;
  detailLines?: string[];
  testIdSuffix: string;
  /** Балл скоркарты 0–100 — для строки тултипа «балл N из 100». */
  score?: number;
  details?: ReliabilityDetailsSource;
}) {
  const interactive = details !== undefined;
  const scoreLine = interactive
    ? score !== undefined
      ? `Балл ${Math.round(score)} из 100, подробности по клику`
      : 'Подробности по клику'
    : null;

  const tooltipLabel = (
    <Stack gap={2} data-testid={`reliability-tooltip-${testIdSuffix}`}>
      {scoreLine && (
        <Text size="xs" fw={600}>
          {scoreLine}
        </Text>
      )}
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
  );

  if (!interactive) {
    return (
      <Tooltip multiline w={280} withArrow label={tooltipLabel}>
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
            backgroundColor: RELIABILITY_COLOR[reliability],
            flexShrink: 0,
          }}
        />
      </Tooltip>
    );
  }

  return (
    <InteractiveReliabilityDot
      reliability={reliability}
      tooltipLabel={tooltipLabel}
      testIdSuffix={testIdSuffix}
      details={details}
    />
  );
}

/** Кнопка-светофор + Popover (desktop) / Drawer (mobile) с <see>ReliabilityDetails</see>. */
function InteractiveReliabilityDot({
  reliability,
  tooltipLabel,
  testIdSuffix,
  details,
}: {
  reliability: ReliabilityLevel;
  tooltipLabel: React.ReactNode;
  testIdSuffix: string;
  details: ReliabilityDetailsSource;
}) {
  const [opened, { toggle, close }] = useDisclosure(false);
  const isMobile = useMediaQuery('(max-width: 48em)');

  const button = (
    <Tooltip multiline w={280} withArrow label={tooltipLabel} disabled={opened}>
      <Box
        component="button"
        type="button"
        aria-label={`${RELIABILITY_ARIA_LABEL[reliability]}, показать разбивку`}
        aria-expanded={opened}
        data-testid={`reliability-dot-${testIdSuffix}`}
        data-reliability={reliability}
        onClick={(event: React.MouseEvent) => {
          event.stopPropagation();
          toggle();
        }}
        style={{
          // Область нажатия 24×24 (палец), визуально — точка 10px.
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          width: 24,
          height: 24,
          padding: 0,
          margin: -7,
          border: 0,
          background: 'transparent',
          cursor: 'pointer',
          flexShrink: 0,
        }}
      >
        <span
          aria-hidden="true"
          style={{ display: 'block', width: 10, height: 10, borderRadius: '50%', backgroundColor: RELIABILITY_COLOR[reliability] }}
        />
      </Box>
    </Tooltip>
  );

  // Содержимое рендерится в портале, но React-события всплывают по дереву компонентов: без stopPropagation клик внутри
  // карточки «выбрал» бы кандидата/раскрыл строку, в которую вложен светофор.
  const content = (
    <div onClick={(event) => event.stopPropagation()}>
      <ReliabilityDetails signals={details.signals} lookup={details.lookup} testIdSuffix={testIdSuffix} />
    </div>
  );

  if (isMobile) {
    return (
      <>
        {button}
        <Drawer
          opened={opened}
          onClose={close}
          position="bottom"
          size="85%"
          title="Надёжность бумаги"
          data-testid={`reliability-drawer-${testIdSuffix}`}
        >
          {content}
        </Drawer>
      </>
    );
  }

  return (
    <Popover
      opened={opened}
      onChange={(next) => {
        if (!next) close();
      }}
      position="bottom-start"
      width={420}
      withArrow
      shadow="md"
      withinPortal
      // hideDetached по умолчанию прячет дропдаун, когда floating-ui считает опорный элемент «отсоединённым» (в jsdom
      // все прямоугольники нулевые) — отключаем: попап закрывается кликом вне/Escape, а не по геометрии.
      hideDetached={false}
    >
      <Popover.Target>
        <span style={{ display: 'inline-flex' }}>{button}</span>
      </Popover.Target>
      <Popover.Dropdown
        data-testid={`reliability-popover-${testIdSuffix}`}
        style={{ maxWidth: '92vw', maxHeight: '75vh', overflowY: 'auto' }}
      >
        {content}
      </Popover.Dropdown>
    </Popover>
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
export function RiskSignalBadges({
  signals,
  testIdSuffix,
  lookup,
}: {
  signals: RiskSignals;
  testIdSuffix?: string;
  /** secid/ISIN бумаги: разбивка скоркарты запросится по клику, если в `signals` компонентов нет. */
  lookup?: string | null;
}) {
  const suffix = testIdSuffix ? `-${testIdSuffix}` : '';
  const spread = spreadLabel(signals);
  const hasDetails = (signals.components?.length ?? 0) > 0 || Boolean(lookup);

  return (
    <Group gap={4} wrap="wrap" align="center" data-testid={`risk-signal-badges${suffix}`}>
      <ReliabilityDot
        reliability={signals.reliability}
        reliabilityReason={signals.reliabilityReason}
        detailLines={[signals.liquidityLabel, spread]}
        testIdSuffix={testIdSuffix ?? 'default'}
        score={signals.score}
        details={hasDetails ? { signals, lookup } : undefined}
      />
      {signals.valueVerdict !== undefined && (
        <ValueBadge
          verdict={signals.valueVerdict}
          gSpreadFraction={signals.gSpreadFraction}
          medianFraction={signals.valueMedianFraction}
          thresholdFraction={signals.valueThresholdFraction}
          testIdSuffix={testIdSuffix ?? 'default'}
        />
      )}
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
      Светофор надёжности — скоркарта (клик по точке открывает разбивку), «дёшево/дорого» — отдельная
      ось цены. {RELIABILITY_DISCLAIMER}
    </Text>
  );
}
