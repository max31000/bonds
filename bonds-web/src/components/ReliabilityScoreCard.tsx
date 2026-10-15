import { Badge, Box, Group, Stack, Table, Text } from '@mantine/core';
import type { RiskSignals, ScoreComponent } from '../api/types';
import { formatBp } from '../utils/format';
import { RELIABILITY_ARIA_LABEL, RELIABILITY_COLOR, RELIABILITY_TEXT } from './reliabilityLight';
import { CRITICAL_FLAG_LABELS, RELIABILITY_DISCLAIMER, WARNING_FLAG_LABELS, flagLabel } from './reliabilityCopy';
import { ValueBadge } from './ValueBadge';

/** Слои скоркарты в порядке показа; `layer` компонента зеркалит backend (`A` структурный, `B` фундаментальный, `C` рыночный). */
const LAYERS: { layer: ScoreComponent['layer']; title: string }[] = [
  { layer: 'A', title: 'Структура выпуска' },
  { layer: 'B', title: 'Отчётность эмитента' },
  { layer: 'C', title: 'Рынок' },
];

/**
 * Балл набранных баллов по слою (только компоненты с данными — `missing` в сумму не входят, как и в скоре):
 * «набрано / доступно».
 */
function layerTotals(components: ScoreComponent[]): { points: number; max: number } {
  return components
    .filter((c) => !c.missing)
    .reduce((acc, c) => ({ points: acc.points + c.points, max: acc.max + c.maxPoints }), { points: 0, max: 0 });
}

function ComponentRow({ component, testIdSuffix }: { component: ScoreComponent; testIdSuffix: string }) {
  const testId = `score-component-${component.key}-${testIdSuffix}`;

  if (component.missing) {
    // «Нет данных» — серая строка: компонент не входит в скор и НЕ равен нулю.
    return (
      <Table.Tr data-testid={testId} data-missing="true" style={{ opacity: 0.7 }}>
        <Table.Td>
          <Text size="sm" c="dimmed">
            {component.title}
          </Text>
          <Text size="xs" c="dimmed">
            {component.source}
          </Text>
        </Table.Td>
        <Table.Td>
          <Text size="xs" c="dimmed">
            нет данных{component.rawDisplay ? `: ${stripMissingPrefix(component.rawDisplay)}` : ''}
          </Text>
        </Table.Td>
        <Table.Td ta="right">
          <Text size="xs" c="dimmed">
            — / {component.maxPoints}
          </Text>
        </Table.Td>
      </Table.Tr>
    );
  }

  return (
    <Table.Tr data-testid={testId}>
      <Table.Td>
        <Text size="sm">{component.title}</Text>
        <Text size="xs" c="dimmed">
          {component.source}
        </Text>
      </Table.Td>
      <Table.Td>
        <Text size="sm">{component.rawDisplay ?? '—'}</Text>
        {component.stale && (
          <Badge size="xs" color="yellow" variant="light" data-testid={`${testId}-stale`}>
            отчётность устарела
          </Badge>
        )}
      </Table.Td>
      <Table.Td ta="right">
        <Text size="sm" fw={600} className="mvv-num">
          {component.points} / {component.maxPoints}
        </Text>
      </Table.Td>
    </Table.Tr>
  );
}

/** Бэкенд кладёт в `rawDisplay` пропущенных компонентов «нет данных: причина» — префикс уже показан слева. */
function stripMissingPrefix(text: string): string {
  return text.replace(/^нет данных:?\s*/i, '');
}

/**
 * Скоркарта надёжности бумаги: «один взгляд — балл и светофор, один клик — из чего он сложился»
 * (задача 47). Шапка: светофор + балл крупно + покрытие данными; критические флаги красными бейджами с
 * человекочитаемыми подписями (предупреждения — жёлтыми); тело — компоненты по слоям (структура выпуска /
 * отчётность эмитента / рынок): значение, баллы, источник; «нет данных» — серая строка; подвал — ось Value
 * (отдельный бейдж, НЕ часть балла) и дисклеймер «не кредитный рейтинг и не рекомендация».
 * <para>Единицы: `score` — 0–100, `coverage` — ДОЛЯ 0–1 (в процент переводится здесь один раз), G-спред/медиана/
 * порог — ДОЛИ (б.п. рисует `formatBp`).</para>
 */
export function ReliabilityScoreCard({
  riskSignals,
  testIdSuffix = 'card',
}: {
  riskSignals: RiskSignals;
  testIdSuffix?: string;
}) {
  const { reliability, reliabilityReason, score, coverage } = riskSignals;
  const components = riskSignals.components ?? [];
  const critical = riskSignals.criticalFlags ?? [];
  const warnings = riskSignals.warningFlags ?? [];
  const coveragePercent = coverage === undefined ? null : Math.round(coverage * 100);

  return (
    <Stack gap="sm" data-testid={`reliability-score-card-${testIdSuffix}`}>
      <Group gap="md" wrap="nowrap" align="center">
        <Box
          role="img"
          aria-label={RELIABILITY_ARIA_LABEL[reliability]}
          data-testid={`score-card-light-${testIdSuffix}`}
          data-reliability={reliability}
          style={{ width: 18, height: 18, borderRadius: '50%', flexShrink: 0, backgroundColor: RELIABILITY_COLOR[reliability] }}
        />
        <Group gap={6} align="baseline" wrap="nowrap">
          <Text fz={32} fw={700} lh={1} className="mvv-num" data-testid={`score-card-score-${testIdSuffix}`}>
            {score === undefined ? '—' : Math.round(score)}
          </Text>
          <Text size="sm" c="dimmed">
            из 100
          </Text>
        </Group>
        <Stack gap={0}>
          <Text size="sm" fw={600}>
            {RELIABILITY_TEXT[reliability]}
          </Text>
          <Text size="xs" c="dimmed" data-testid={`score-card-coverage-${testIdSuffix}`}>
            {coveragePercent === null ? 'данных: —' : `данных: ${coveragePercent}%`}
          </Text>
        </Stack>
      </Group>

      <Text size="xs" c="dimmed" data-testid={`score-card-reason-${testIdSuffix}`}>
        {reliabilityReason}
      </Text>

      {(critical.length > 0 || warnings.length > 0) && (
        <Group gap={4} wrap="wrap" data-testid={`score-card-flags-${testIdSuffix}`}>
          {critical.map((flag) => (
            <Badge key={flag} color="red" variant="filled" data-testid={`score-flag-critical-${flag}`}>
              {CRITICAL_FLAG_LABELS[flag] ?? flagLabel(flag)}
            </Badge>
          ))}
          {warnings.map((flag) => (
            <Badge key={flag} color="yellow" variant="light" data-testid={`score-flag-warning-${flag}`}>
              {WARNING_FLAG_LABELS[flag] ?? flagLabel(flag)}
            </Badge>
          ))}
        </Group>
      )}

      {LAYERS.map(({ layer, title }) => {
        const layerComponents = components.filter((c) => c.layer === layer);
        if (layerComponents.length === 0) return null;
        const totals = layerTotals(layerComponents);
        const stale = layerComponents.some((c) => c.stale && !c.missing);
        return (
          <Stack key={layer} gap={4} data-testid={`score-layer-${layer}-${testIdSuffix}`}>
            <Group justify="space-between" gap="xs" wrap="nowrap">
              <Text size="sm" fw={600}>
                {title}
              </Text>
              <Group gap={6} wrap="nowrap">
                {stale && (
                  <Badge size="xs" color="yellow" variant="light">
                    отчётность устарела
                  </Badge>
                )}
                <Text size="xs" c="dimmed" className="mvv-num">
                  {totals.max > 0 ? `${totals.points} / ${totals.max}` : 'нет данных'}
                </Text>
              </Group>
            </Group>
            <Table withRowBorders verticalSpacing={4} horizontalSpacing={6} fz="sm" layout="fixed" style={{ overflowWrap: 'anywhere' }}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w="46%">Показатель</Table.Th>
                  <Table.Th>Значение</Table.Th>
                  <Table.Th w={64} ta="right">
                    Баллы
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {layerComponents.map((c) => (
                  <ComponentRow key={c.key} component={c} testIdSuffix={testIdSuffix} />
                ))}
              </Table.Tbody>
            </Table>
          </Stack>
        );
      })}

      {components.length === 0 && (
        <Text size="xs" c="dimmed" data-testid={`score-card-no-components-${testIdSuffix}`}>
          Разбивка по компонентам недоступна.
        </Text>
      )}

      {riskSignals.valueVerdict !== undefined && (
        <Group gap="xs" wrap="wrap" align="center" data-testid={`score-card-value-${testIdSuffix}`}>
          <Text size="xs" c="dimmed">
            Цена (не входит в балл):
          </Text>
          <ValueBadge
            verdict={riskSignals.valueVerdict}
            gSpreadFraction={riskSignals.gSpreadFraction}
            medianFraction={riskSignals.valueMedianFraction}
            thresholdFraction={riskSignals.valueThresholdFraction}
            testIdSuffix={`card-${testIdSuffix}`}
          />
          {riskSignals.gSpreadFraction !== null && (
            <Text size="xs" c="dimmed">
              G-спред {formatBp(riskSignals.gSpreadFraction)}
            </Text>
          )}
        </Group>
      )}

      <Text size="xs" c="dimmed" data-testid={`score-card-disclaimer-${testIdSuffix}`}>
        {RELIABILITY_DISCLAIMER}
      </Text>
    </Stack>
  );
}
