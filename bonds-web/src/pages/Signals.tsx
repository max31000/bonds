import { useEffect, useState } from 'react';
import {
  Stack,
  Paper,
  Text,
  Loader,
  Badge,
  Group,
  Button,
  SegmentedControl,
} from '@mantine/core';
import { EmptyState, ErrorState, Page, Section } from '@mvv42/kit';
import { useSignalsStore } from '../store/useSignalsStore';
import { formatDate } from '../utils/format';
import type { Signal, SignalSeverity } from '../api/types';

const SEVERITY_LABEL: Record<SignalSeverity, string> = {
  High: 'Высокая',
  Medium: 'Средняя',
  Low: 'Низкая',
};

const SEVERITY_COLOR: Record<SignalSeverity, string> = {
  High: 'red',
  Medium: 'orange',
  Low: 'gray',
};

const TYPE_LABEL: Record<string, string> = {
  Coupon: 'Купон',
  Amortization: 'Амортизация',
  Maturity: 'Погашение',
  OfferPut: 'Оферта (put)',
  OfferCall: 'Оферта (call)',
  FloaterRateReset: 'Пересчёт ставки флоатера',
  UninvestedCash: 'Незаинвестированный кэш',
  YieldBelowAlternative: 'Доходность ниже альтернативы',
  Concentration: 'Концентрация по эмитенту',
  DurationDrift: 'Дрейф дюрации',
};

function SignalCard({ signal, onMarkRead }: { signal: Signal; onMarkRead: (id: number) => void }) {
  return (
    <Paper withBorder p="md" radius="md" data-testid={`signal-${signal.id}`}>
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Stack gap={4}>
          <Group gap="xs">
            <Badge color={SEVERITY_COLOR[signal.severity]} variant="filled" size="sm">
              {SEVERITY_LABEL[signal.severity]}
            </Badge>
            <Text fw={600} size="sm">
              {TYPE_LABEL[signal.type] ?? signal.type}
            </Text>
            {!signal.isRead && (
              <Badge color="violet" variant="light" size="xs">
                новое
              </Badge>
            )}
          </Group>
          <Text size="sm">{signal.suggestedAction}</Text>
          <Text size="xs" c="dimmed">
            {formatDate(signal.date)}
            {signal.positionId !== null ? ` · позиция #${signal.positionId}` : ''}
          </Text>
        </Stack>
        {!signal.isRead && (
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

type FilterMode = 'all' | 'unread';

/**
 * Панель сигналов: купоны/амортизации/оферты/концентрация/дрейф дюрации и т.д. (спека §8).
 * Высокая важность визуально выделена (badge), отметка прочитанным обновляет список (09c §B.6).
 */
export function Signals() {
  const { signals, isLoading, error, load, markRead } = useSignalsStore();
  const [filter, setFilter] = useState<FilterMode>('all');

  useEffect(() => {
    load();
  }, [load]);

  const visible = filter === 'unread' ? signals.filter((s) => !s.isRead) : signals;
  // High-severity первыми, затем по дате (свежие сверху).
  const sorted = [...visible].sort((a, b) => {
    if (a.severity !== b.severity) {
      const rank: Record<SignalSeverity, number> = { High: 0, Medium: 1, Low: 2 };
      return rank[a.severity] - rank[b.severity];
    }
    return b.date.localeCompare(a.date);
  });

  return (
    <Page
      title="Сигналы"
      description="Оферты, купоны, концентрация и другие события портфеля"
      actions={
        <SegmentedControl
          size="xs"
          value={filter}
          onChange={(v) => setFilter(v as FilterMode)}
          data={[
            { value: 'all', label: 'Все' },
            { value: 'unread', label: 'Непрочитанные' },
          ]}
        />
      }
    >
      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить сигналы" error={error} data-testid="signals-error" />
        </Section>
      )}

      {!isLoading && !error && sorted.length === 0 && (
        <Section>
          <EmptyState
            title="Нет сигналов"
            description={filter === 'unread' ? 'Все сигналы прочитаны.' : 'Сигналов пока нет.'}
            data-testid="signals-empty"
          />
        </Section>
      )}

      {!isLoading && !error && sorted.length > 0 && (
        <Stack gap="sm" data-testid="signals-list">
          {sorted.map((signal) => (
            <SignalCard key={signal.id} signal={signal} onMarkRead={markRead} />
          ))}
        </Stack>
      )}
    </Page>
  );
}
