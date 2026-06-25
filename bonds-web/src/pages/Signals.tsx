import { useEffect, useState } from 'react';
import {
  Title,
  Stack,
  Paper,
  Text,
  Alert,
  Loader,
  Center,
  Badge,
  Group,
  Button,
  SegmentedControl,
} from '@mantine/core';
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
    <Stack gap="md">
      <Group justify="space-between">
        <Title order={2}>Сигналы</Title>
        <SegmentedControl
          size="xs"
          value={filter}
          onChange={(v) => setFilter(v as FilterMode)}
          data={[
            { value: 'all', label: 'Все' },
            { value: 'unread', label: 'Непрочитанные' },
          ]}
        />
      </Group>

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить сигналы" data-testid="signals-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && sorted.length === 0 && (
        <Alert color="gray" title="Нет сигналов" data-testid="signals-empty">
          {filter === 'unread' ? 'Все сигналы прочитаны.' : 'Сигналов пока нет.'}
        </Alert>
      )}

      {!isLoading && !error && sorted.length > 0 && (
        <Stack gap="sm" data-testid="signals-list">
          {sorted.map((signal) => (
            <SignalCard key={signal.id} signal={signal} onMarkRead={markRead} />
          ))}
        </Stack>
      )}
    </Stack>
  );
}
