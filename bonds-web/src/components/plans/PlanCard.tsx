import { useState } from 'react';
import { Badge, Button, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import type { Plan, PlanLine } from '../../api/types';
import type { PlanDecisionResult } from '../../store/usePlansStore';
import { formatDateTime } from '../../utils/format';
import {
  formatLimitPrice,
  formatPlanMoney,
  formatQuantity,
  isDecidable,
  PLAN_RULE_LABEL,
  PLAN_STATUS_COLOR,
  PLAN_STATUS_LABEL,
} from '../../utils/plans';

function Figure({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div>
      <div className="bonds-eyebrow">{label}</div>
      <Text fw={600} data-testid={testId}>
        {value}
      </Text>
    </div>
  );
}

/** Строка плана = будущая лимитная заявка (только данные): сторона, бумага, лоты, лимитная цена, сумма, комиссия, налог и причина. */
function LineRow({ planId, line }: { planId: number; line: PlanLine }) {
  const buy = line.side === 'Buy';
  return (
    <Stack gap={4} className="bonds-plan-line" data-testid={`plan-${planId}-line-${line.lineNo}`}>
      <Group justify="space-between" wrap="wrap" gap="xs">
        <Group gap="xs">
          <Badge variant="light" color={buy ? 'green' : 'red'}>
            {buy ? 'Покупка' : 'Продажа'}
          </Badge>
          <Text fw={600}>{line.ticker}</Text>
          <Text size="xs" c="dimmed">
            {line.broker === 'freedom' ? 'Freedom' : 'T-Invest'}
          </Text>
        </Group>
        <Text size="sm" data-testid={`plan-${planId}-line-${line.lineNo}-qty`}>
          {formatQuantity(line)}
        </Text>
      </Group>
      <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="xs">
        <Figure label="Лимитная цена" value={formatLimitPrice(line)} testId={`plan-${planId}-line-${line.lineNo}-price`} />
        <Figure label="Сумма" value={formatPlanMoney(line.estAmount, line.currency)} testId={`plan-${planId}-line-${line.lineNo}-amount`} />
        <Figure label="Комиссия" value={formatPlanMoney(line.estFee, line.currency)} />
        <Figure label="Налог" value={line.side === 'Sell' ? formatPlanMoney(line.estTax, line.currency) : '—'} />
      </SimpleGrid>
      {line.reason && (
        <Text size="xs" c="dimmed">
          {line.reason}
        </Text>
      )}
    </Stack>
  );
}

/**
 * Карточка плана (задача 63): правило, причина, строки (будущие лимитные заявки), итоги, статус. РЕЖИМ НАБЛЮДЕНИЯ — «Подтвердить» лишь помечает план
 * («не исполнен: dry-run»), брокеру ничего не отправляется. Суммы строк — в валюте строки, итоги — в валюте портфеля плана.
 */
export function PlanCard({
  plan,
  busy,
  onApprove,
  onReject,
}: {
  plan: Plan;
  busy: boolean;
  onApprove: (id: number) => Promise<PlanDecisionResult>;
  onReject: (id: number) => Promise<PlanDecisionResult>;
}) {
  const [error, setError] = useState<string | null>(null);
  const summary = plan.summary;
  const currency = summary?.currency ?? plan.lines[0]?.currency ?? 'RUB';

  const decide = async (action: (id: number) => Promise<PlanDecisionResult>) => {
    setError(null);
    const result = await action(plan.id);
    if (!result.ok) setError(result.error);
  };

  return (
    <Stack gap="xs" p="md" className="bonds-plan-card" data-testid={`plan-${plan.id}`}>
      <Group justify="space-between" wrap="wrap" gap="xs">
        <Stack gap={2}>
          <Text fw={600}>{PLAN_RULE_LABEL[plan.rule] ?? plan.rule}</Text>
          <Text size="sm" c="dimmed" data-testid={`plan-${plan.id}-trigger`}>
            {plan.trigger}
          </Text>
        </Stack>
        <Badge variant="light" color={PLAN_STATUS_COLOR[plan.status]} data-testid={`plan-${plan.id}-status`}>
          {PLAN_STATUS_LABEL[plan.status]}
        </Badge>
      </Group>

      <Text size="xs" c="dimmed">
        Создан {formatDateTime(plan.createdAt)}
        {isDecidable(plan.status) && ` · действует до ${formatDateTime(plan.expiresAt)}`}
        {plan.decidedAt && ` · решение ${formatDateTime(plan.decidedAt)} (${plan.decidedVia ?? '—'})`}
      </Text>

      {summary && (
        <SimpleGrid cols={{ base: 2, sm: 5 }} spacing="sm">
          <Figure label="Деньги до" value={formatPlanMoney(summary.cashBefore, currency)} testId={`plan-${plan.id}-cash-before`} />
          <Figure label="Деньги после" value={formatPlanMoney(summary.cashAfter, currency)} testId={`plan-${plan.id}-cash-after`} />
          <Figure label="Покупки" value={formatPlanMoney(summary.buyTotal, currency)} />
          <Figure label="Продажи" value={formatPlanMoney(summary.sellTotal, currency)} />
          <Figure label="Комиссия · налог" value={`${formatPlanMoney(summary.feeTotal, currency)} · ${formatPlanMoney(summary.taxTotal, currency)}`} />
        </SimpleGrid>
      )}
      {summary?.requestedNet != null && (
        <Text size="sm" data-testid={`plan-${plan.id}-net`}>
          К выводу после налога и комиссии: <b>{formatPlanMoney(summary.netToWithdraw, currency)}</b> из запрошенных {formatPlanMoney(summary.requestedNet, currency)}
          {summary.shortfall != null && summary.shortfall > 0 && (
            <Text span c="red">
              {' '}
              (не хватает {formatPlanMoney(summary.shortfall, currency)})
            </Text>
          )}
        </Text>
      )}

      {summary?.pricesPossiblyStale && (
        <Text size="xs" c="yellow" data-testid={`plan-${plan.id}-stale`}>
          Построен вне часов торгов: цены стакана могли устареть.
        </Text>
      )}
      {summary?.warnings.map((w) => (
        <Text key={w} size="xs" c="dimmed">
          ⚠ {w}
        </Text>
      ))}
      {summary?.notes.map((n) => (
        <Text key={n} size="xs" c="dimmed">
          {n}
        </Text>
      ))}
      {summary && summary.skipped.length > 0 && (
        <Text size="xs" c="dimmed" data-testid={`plan-${plan.id}-skipped`}>
          Пропущено: {summary.skipped.map((s) => `${s.ticker} — ${s.reason}`).join('; ')}
        </Text>
      )}

      <Stack gap={0}>
        {plan.lines.map((line) => (
          <LineRow key={line.lineNo} planId={plan.id} line={line} />
        ))}
      </Stack>

      {plan.status === 'Approved' && summary?.executionNote && (
        <Text size="sm" c="dimmed" data-testid={`plan-${plan.id}-note`}>
          {summary.executionNote}
        </Text>
      )}
      {error && (
        <Text size="sm" c="red" data-testid={`plan-${plan.id}-error`}>
          {error}
        </Text>
      )}
      {isDecidable(plan.status) && (
        <Group gap="xs">
          <Button size="xs" disabled={busy} onClick={() => void decide(onApprove)} data-testid={`plan-${plan.id}-approve`}>
            Подтвердить
          </Button>
          <Button size="xs" variant="default" disabled={busy} onClick={() => void decide(onReject)} data-testid={`plan-${plan.id}-reject`}>
            Отклонить
          </Button>
        </Group>
      )}
    </Stack>
  );
}
