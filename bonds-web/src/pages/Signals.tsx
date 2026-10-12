import { useEffect, useState } from 'react';
import { Stack, Paper, Text, Loader, Badge, Group, Button, Collapse } from '@mantine/core';
import { EmptyState, ErrorState, Page, Section } from '@mvv42/kit';
import { useSignalsStore } from '../store/useSignalsStore';
import { formatDate } from '../utils/format';
import type { Signal, SignalSeverity, SignalType } from '../api/types';

/** Порядок групп: самое срочное сверху. */
const SEVERITY_ORDER: SignalSeverity[] = ['Critical', 'Warning', 'Info'];

const SEVERITY_LABEL: Record<SignalSeverity, string> = {
  Critical: 'Требует решения',
  Warning: 'Важно',
  Info: 'К сведению',
};

const SEVERITY_COLOR: Record<SignalSeverity, string> = {
  Critical: 'red',
  Warning: 'orange',
  Info: 'gray',
};

const TYPE_LABEL: Record<SignalType, string> = {
  UpcomingOffer: 'Оферта',
  UpcomingRedemption: 'Погашение',
  UpcomingAmortization: 'Амортизация',
  FloaterRateReset: 'Пересчёт ставки флоатера',
  UninvestedCashThreshold: 'Свободные деньги',
  ConcentrationLimitBreached: 'Концентрация по эмитенту',
};

/** Ближайшая дата события первой; «состояния» без даты — в конце группы. */
function byEventDate(a: Signal, b: Signal): number {
  if (a.eventDate === b.eventDate) return a.id - b.id;
  if (a.eventDate === null) return 1;
  if (b.eventDate === null) return -1;
  return a.eventDate.localeCompare(b.eventDate);
}

function SignalCard({
  signal,
  onMarkRead,
}: {
  signal: Signal;
  onMarkRead?: (id: number) => void;
}) {
  return (
    <Paper withBorder p="md" radius="md" data-testid={`signal-${signal.id}`}>
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Stack gap={4}>
          <Group gap="xs">
            <Badge color={SEVERITY_COLOR[signal.severity]} variant="filled" size="sm">
              {SEVERITY_LABEL[signal.severity]}
            </Badge>
            <Text fw={600} size="sm">
              {TYPE_LABEL[signal.type]}
            </Text>
            {!signal.isRead && !signal.isResolved && (
              <Badge color="violet" variant="light" size="xs">
                новое
              </Badge>
            )}
          </Group>
          {signal.subject && (
            <Text fw={500} size="sm" data-testid={`signal-subject-${signal.id}`}>
              {signal.subject}
            </Text>
          )}
          {signal.suggestedAction && <Text size="sm">{signal.suggestedAction}</Text>}
          <Text size="xs" c="dimmed">
            {signal.eventDate ? `Дата события: ${formatDate(signal.eventDate)}` : 'Текущее состояние'}
            {signal.isResolved && signal.resolvedAt ? ` · закрыто ${formatDate(signal.resolvedAt)}` : ''}
          </Text>
        </Stack>
        {onMarkRead && !signal.isRead && !signal.isResolved && (
          <Button
            size="xs"
            variant="light"
            onClick={() => onMarkRead(signal.id)}
            data-testid={`mark-read-${signal.id}`}
          >
            Прочитано
          </Button>
        )}
      </Group>
    </Paper>
  );
}

/**
 * «События» (plan/40): короткий список ТЕКУЩИХ событий портфеля — оферты, погашения, амортизации,
 * пересчёт ставки флоатеров, концентрация, свободные деньги. Группы по важности, внутри — по дате
 * события. «Прочитано» подтверждает событие (оно остаётся в списке, но без бейджа «новое»);
 * закрытые движком события живут в свёрнутой «Истории». Формулировки — оценки/кандидаты, не советы.
 */
export function Signals() {
  const { signals, history, historyLoaded, isLoading, error, load, loadHistory, markRead, markAllRead } =
    useSignalsStore();
  const [historyOpen, setHistoryOpen] = useState(false);

  useEffect(() => {
    load();
  }, [load]);

  const toggleHistory = () => {
    const next = !historyOpen;
    setHistoryOpen(next);
    if (next) loadHistory();
  };

  const hasUnread = signals.some((s) => !s.isRead);

  return (
    <Page
      title="События"
      description="Что требует внимания в портфеле: оферты, погашения, концентрация, свободные деньги. Оценки, не инвестиционные рекомендации."
      actions={
        hasUnread ? (
          <Button size="xs" variant="light" onClick={markAllRead} data-testid="mark-all-read">
            Всё прочитано
          </Button>
        ) : undefined
      }
    >
      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить события" error={error} data-testid="signals-error" />
        </Section>
      )}

      {!isLoading && !error && signals.length === 0 && (
        <Section>
          <EmptyState
            title="Ничего не требует внимания"
            description="Приближающихся оферт, погашений и превышений лимитов нет."
            data-testid="signals-empty"
          />
        </Section>
      )}

      {!isLoading && !error && signals.length > 0 && (
        <Stack gap="lg" data-testid="signals-list">
          {SEVERITY_ORDER.map((severity) => {
            const group = signals.filter((s) => s.severity === severity).sort(byEventDate);
            if (group.length === 0) return null;
            return (
              <Stack key={severity} gap="sm" data-testid={`signals-group-${severity}`}>
                <Text fw={600} size="sm" c="dimmed">
                  {SEVERITY_LABEL[severity]} · {group.length}
                </Text>
                {group.map((signal) => (
                  <SignalCard key={signal.id} signal={signal} onMarkRead={markRead} />
                ))}
              </Stack>
            );
          })}
        </Stack>
      )}

      <Stack gap="sm" mt="xl">
        <Button
          variant="subtle"
          size="xs"
          onClick={toggleHistory}
          data-testid="history-toggle"
          style={{ alignSelf: 'flex-start' }}
        >
          {historyOpen ? 'Скрыть историю' : 'История'}
        </Button>
        <Collapse expanded={historyOpen}>
          <Stack gap="sm" data-testid="signals-history">
            {historyLoaded && history.length === 0 && (
              <Text size="sm" c="dimmed" data-testid="history-empty">
                Закрытых событий пока нет.
              </Text>
            )}
            {history.map((signal) => (
              <SignalCard key={signal.id} signal={signal} />
            ))}
          </Stack>
        </Collapse>
      </Stack>
    </Page>
  );
}
