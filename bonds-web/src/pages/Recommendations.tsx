import { useEffect, useState } from 'react';
import {
  Title,
  Stack,
  Paper,
  Text,
  Badge,
  Alert,
  Loader,
  Center,
  Group,
  Button,
  NumberInput,
  SimpleGrid,
  Table,
  TextInput,
  ActionIcon,
} from '@mantine/core';
import { useRecommendationsStore } from '../store/useRecommendationsStore';
import { useWatchlistStore } from '../store/useWatchlistStore';
import { Disclaimer } from '../components/Disclaimer';
import { formatRub, formatPercent, formatNumber, formatDate, commissionSourceLabel } from '../utils/format';
import type { ComparisonRow } from '../api/types';

/** Карточка одного sell-кандидата с бейджами-причинами (plan/17 §A.1). Формулировки — оценочные («кандидат»), не «продайте». */
function SellCandidateCard({ row, reasons }: { row: ComparisonRow; reasons: { kind: string; label: string }[] }) {
  return (
    <Paper withBorder p="md" radius="md" data-testid={`sell-candidate-${row.positionId}`}>
      <Group justify="space-between" mb="xs">
        <Text fw={600}>{row.name ?? row.issuer ?? `Позиция #${row.positionId}`}</Text>
        <Text size="sm" c="dimmed">
          {formatPercent(row.effectiveYield)}
        </Text>
      </Group>
      <Group gap={6} wrap="wrap">
        {reasons.map((reason) => (
          <Badge key={reason.kind} size="sm" color="orange" variant="light">
            {reason.label}
          </Badge>
        ))}
        {reasons.length === 0 && (
          <Badge size="sm" color="gray" variant="light">
            кандидат на сравнение
          </Badge>
        )}
      </Group>
    </Paper>
  );
}

/** Строка «вне сравнения» — floater/indexed/dataIncomplete не участвуют в ранжировании доходности (spec §6). */
function OutOfComparisonRow({ row }: { row: ComparisonRow }) {
  return (
    <Paper withBorder p="sm" radius="md" data-testid={`out-of-comparison-${row.positionId}`}>
      <Group justify="space-between">
        <Text size="sm">{row.name ?? row.issuer ?? `Позиция #${row.positionId}`}</Text>
        <Badge size="sm" color="gray" variant="outline">
          вне сравнения
        </Badge>
      </Group>
    </Paper>
  );
}

function WeakLinksSection() {
  const { sellCandidates, outOfComparison, comparisonDisclaimer, isLoading, error } = useRecommendationsStore();

  return (
    <Paper withBorder p="md" radius="md" data-testid="weak-links-section">
      <Title order={4} mb="sm">
        Слабые звенья — кандидаты на пересмотр
      </Title>
      <Text size="xs" c="dimmed" mb="sm">
        Оценка по доходности относительно медианы портфеля, горизонта и концентрации по эмитенту —
        это аналитическая подборка, не инвестиционная рекомендация продавать что-либо.
      </Text>

      {isLoading && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" data-testid="weak-links-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && sellCandidates.length === 0 && outOfComparison.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="weak-links-empty">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      )}

      {!isLoading && !error && sellCandidates.length > 0 && (
        <Stack gap="sm">
          {sellCandidates.map((c) => (
            <SellCandidateCard key={c.row.positionId} row={c.row} reasons={c.reasons} />
          ))}
        </Stack>
      )}

      {!isLoading && !error && outOfComparison.length > 0 && (
        <Stack gap="xs" mt="md">
          <Text size="xs" fw={600} c="dimmed">
            Вне сравнения (флоатеры/индексируемые/неполные данные — доходность несравнима с YTM)
          </Text>
          {outOfComparison.map((row) => (
            <OutOfComparisonRow key={row.positionId} row={row} />
          ))}
        </Stack>
      )}

      <Disclaimer text={comparisonDisclaimer} />
    </Paper>
  );
}

/** Горизонт замены теперь не фиксирован (min daysToHorizon пары) — показываем в месяцах до года, иначе в годах. */
function formatHorizon(years: number): string {
  if (years < 1) return `${Math.max(1, Math.round(years * 12))} мес`;
  return `${years.toFixed(1)} г.`;
}

function ReplacementsSection() {
  const { replacements, isLoading, error, sellCandidates } = useRecommendationsStore();

  return (
    <Paper withBorder p="md" radius="md" data-testid="replacements-section">
      <Title order={4} mb="sm">
        Замены — оценка «держать vs переложиться»
      </Title>
      <Text size="xs" c="dimmed" mb="sm">
        Комиссии обеих сделок учтены; показаны только пары с положительной оценкой выгоды.
      </Text>

      {isLoading && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" data-testid="replacements-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && replacements.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="replacements-empty">
          {sellCandidates.length === 0
            ? 'Нет данных для оценки замен.'
            : 'Среди рассмотренных пар нет замены с положительной оценкой выгоды.'}
        </Text>
      )}

      {!isLoading && !error && replacements.length > 0 && (
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
          {replacements.map((pair) => (
            <Paper
              key={`${pair.holdPositionId}-${pair.targetPositionId}`}
              withBorder
              p="sm"
              radius="md"
              data-testid={`replacement-${pair.holdPositionId}-${pair.targetPositionId}`}
            >
              <Text fw={600} size="sm">
                {pair.holdName} → {pair.targetName}
              </Text>
              <Text size="sm" c="teal" fw={600}>
                выгода ≈ {formatRub(pair.result.netBenefitRub)} за {formatHorizon(pair.result.horizonYears)}
              </Text>
              <Text size="xs" c="dimmed">
                комиссии учтены: {formatRub(pair.result.totalSwitchCostRub)}
                {pair.result.breakEvenYears !== null && (
                  <>, окупится за {formatNumber(pair.result.breakEvenYears * 12, 0)} мес</>
                )}
              </Text>
              <Text size="xs" c="dimmed">
                комиссия {formatPercent(pair.result.sellCommissionRateUsed)} — {commissionSourceLabel(pair.result.commissionRateSource)}
              </Text>
            </Paper>
          ))}
        </SimpleGrid>
      )}

      {!isLoading && !error && replacements.length > 0 && (
        <Disclaimer text={replacements[0].result.disclaimer} />
      )}
    </Paper>
  );
}

function AllocationSection() {
  const {
    allocationAmount,
    allocation,
    isAllocationLoading,
    allocationError,
    setAllocationAmount,
    loadAllocation,
  } = useRecommendationsStore();

  return (
    <Paper withBorder p="md" radius="md" data-testid="allocation-section">
      <Title order={4} mb="sm">
        Куда вложить сумму
      </Title>
      <Group align="flex-end" mb="sm" wrap="wrap">
        <NumberInput
          label="Сумма, ₽"
          min={1}
          value={allocationAmount}
          onChange={(v) => setAllocationAmount(typeof v === 'number' ? v : Number(v) || 0)}
          data-testid="allocation-amount-input"
          w={200}
        />
        <Button
          onClick={() => loadAllocation()}
          loading={isAllocationLoading}
          data-testid="allocation-submit-button"
        >
          Рассчитать
        </Button>
      </Group>

      {isAllocationLoading && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isAllocationLoading && allocationError && (
        <Alert color="red" data-testid="allocation-error">
          {allocationError}
        </Alert>
      )}

      {!isAllocationLoading && !allocationError && allocation && (
        <Stack gap="sm" data-testid="allocation-result">
          {allocation.allocations.length === 0 ? (
            <Text size="sm" c="dimmed" data-testid="allocation-empty">
              Ни одна бумага из портфеля не подошла для докупки на эту сумму.
            </Text>
          ) : (
            allocation.allocations.map((line) => {
              const pricePerBondRub = line.estimatedCostRub / line.quantity;
              const cleanPricePerBondRub = line.cleanCostRub / line.quantity;
              const accruedPerBondRub = line.accruedCostRub / line.quantity;
              const commissionPerBondRub = line.commissionCostRub / line.quantity;
              return (
                <Paper key={line.instrumentId} withBorder p="sm" radius="md" data-testid={`allocation-line-${line.instrumentId}`}>
                  <Group justify="space-between">
                    <Text fw={500}>{line.name ?? line.issuer ?? `Инструмент #${line.instrumentId}`}</Text>
                    <Text size="sm">{formatPercent(line.effectiveYield)}</Text>
                  </Group>
                  <Text size="sm" c="dimmed">
                    купить {line.quantity} шт × {formatRub(pricePerBondRub)} (цена {formatRub(cleanPricePerBondRub)} + НКД{' '}
                    {formatRub(accruedPerBondRub)} + комиссия {formatRub(commissionPerBondRub)}), потратите{' '}
                    {formatRub(line.estimatedCostRub)}
                    {line.lotSizeAssumed && ' (лот принят за 1 бумагу — точный размер лота не определён)'}
                  </Text>
                </Paper>
              );
            })
          )}
          <Text size="sm" fw={600} data-testid="allocation-leftover">
            На счету останется {formatRub(allocation.leftoverRub)}
          </Text>
          <Text size="xs" c="dimmed" data-testid="allocation-commission-source">
            Цена лота учитывает комиссию покупки {formatPercent(allocation.commissionRateUsed)} —{' '}
            {commissionSourceLabel(allocation.commissionRateSource)}
          </Text>
          {allocation.allocations.length > 0 && (
            <Text size="xs" c="dimmed" data-testid="allocation-accrued-note">
              Уплаченный при покупке НКД вернётся ближайшим купоном — это не дополнительные расходы.
            </Text>
          )}
          <Disclaimer text={allocation.disclaimer} />
        </Stack>
      )}
    </Paper>
  );
}

/** Секция «Watchlist» (plan/20 §B.1) — ручной список бумаг вне текущих позиций: ввод ISIN + заметка, таблица метрик, удаление. */
function WatchlistSection() {
  const { items, disclaimer, isLoading, error, isAdding, addError, load, add, remove, clearAddError } =
    useWatchlistStore();
  const [isin, setIsin] = useState('');
  const [note, setNote] = useState('');

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleAdd = async () => {
    if (!isin.trim()) return;
    const ok = await add(isin.trim(), note.trim() || undefined);
    if (ok) {
      setIsin('');
      setNote('');
    }
  };

  return (
    <Paper withBorder p="md" radius="md" data-testid="watchlist-section">
      <Title order={4} mb="sm">
        Watchlist — бумаги вне портфеля
      </Title>
      <Text size="xs" c="dimmed" mb="sm">
        Ручной список ISIN для сравнения с портфелем в тех же координатах (YTM, дюрация, G-спред) —
        не скринер по всей бирже.
      </Text>

      <Group align="flex-end" mb="sm" wrap="wrap">
        <TextInput
          label="ISIN"
          placeholder="RU000A1038V6"
          value={isin}
          onChange={(e) => {
            setIsin(e.currentTarget.value);
            if (addError) clearAddError();
          }}
          data-testid="watchlist-isin-input"
          w={220}
        />
        <TextInput
          label="Заметка (необязательно)"
          value={note}
          onChange={(e) => setNote(e.currentTarget.value)}
          data-testid="watchlist-note-input"
          w={260}
        />
        <Button onClick={handleAdd} loading={isAdding} data-testid="watchlist-add-button">
          Добавить
        </Button>
      </Group>

      {addError && (
        <Alert color="red" mb="sm" data-testid="watchlist-add-error">
          {addError}
        </Alert>
      )}

      {isLoading && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" data-testid="watchlist-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && items.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="watchlist-empty">
          Пока пусто — добавьте ISIN бумаги, за которой хотите наблюдать.
        </Text>
      )}

      {!isLoading && !error && items.length > 0 && (
        <Table.ScrollContainer minWidth={700}>
          <Table striped highlightOnHover data-testid="watchlist-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Бумага</Table.Th>
                <Table.Th>Доходность</Table.Th>
                <Table.Th>Дюрация, лет</Table.Th>
                <Table.Th>G-спред, б.п.</Table.Th>
                <Table.Th>Добавлена</Table.Th>
                <Table.Th>Заметка</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((item) => (
                <Table.Tr key={item.id} data-testid={`watchlist-row-${item.id}`}>
                  <Table.Td>
                    {item.name ?? item.issuer ?? item.isin}
                    {item.dataIncomplete && (
                      <Badge ml={6} size="xs" color="gray" variant="outline">
                        неполные данные
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{formatPercent(item.effectiveYield)}</Table.Td>
                  <Table.Td>{item.modifiedDuration != null ? formatNumber(item.modifiedDuration, 2) : '—'}</Table.Td>
                  <Table.Td>{item.gSpread != null ? formatNumber(item.gSpread, 0) : '—'}</Table.Td>
                  <Table.Td>{formatDate(item.addedAtUtc)}</Table.Td>
                  <Table.Td>{item.note ?? '—'}</Table.Td>
                  <Table.Td>
                    <ActionIcon
                      color="red"
                      variant="subtle"
                      onClick={() => remove(item.id)}
                      data-testid={`watchlist-remove-${item.id}`}
                      aria-label="Удалить из watchlist"
                    >
                      ×
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {!isLoading && !error && items.length > 0 && <Disclaimer text={disclaimer} />}
    </Paper>
  );
}

/**
 * Страница «Рекомендации» (plan/17): три секции — слабые звенья (comparison), замены
 * (replacement) и куда вложить сумму (allocation). Все формулировки — оценочные, не
 * индивидуальные инвестрекомендации (см. Disclaimer/юридическая рамка плана). Задача 20
 * добавляет секцию watchlist (бумаги вне портфеля).
 */
export function Recommendations() {
  const load = useRecommendationsStore((s) => s.load);
  const loadAllocation = useRecommendationsStore((s) => s.loadAllocation);

  useEffect(() => {
    load();
    loadAllocation();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Stack gap="md">
      <Title order={2}>Рекомендации</Title>
      <Text size="sm" c="dimmed">
        Все оценки на этой странице — аналитические, основаны на текущих данных портфеля, не
        индивидуальная инвестиционная рекомендация.
      </Text>

      <WeakLinksSection />
      <ReplacementsSection />
      <AllocationSection />
      <WatchlistSection />
    </Stack>
  );
}
