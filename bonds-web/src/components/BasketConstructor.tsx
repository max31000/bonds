import { useEffect, useState } from 'react';
import {
  Paper,
  Title,
  Text,
  Group,
  Stack,
  NumberInput,
  Button,
  Select,
  Badge,
  Alert,
  Loader,
  Center,
  ActionIcon,
  Table,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { fetchUniverse, postMaterialize } from '../api/universe';
import { fetchAllocation, postBasket } from '../api/recommendations';
import { liquidityLabel, liquidityColor } from '../utils/universeDisplay';
import { formatRub, formatPercent, formatNumber } from '../utils/format';
import { Disclaimer } from './Disclaimer';
import type { BasketResponse, UniverseRow, WhatIfWarning } from '../api/types';

const SEARCH_DEBOUNCE_MS = 300;

/** Одна строка корзины, собираемая пользователем (проценты — UI-конвенция, конвертируются в доли на границе с API, см. doc-comment handleCalculate). */
interface BasketDraftLine {
  instrumentId: number;
  name: string;
  issuer: string | null;
  weightPercent: number;
}

/** Опция выпадашки поиска — переиспользует карточку UniverseRow (тот же паттерн, что MarketComparator/задача 27). */
function UniverseOptionLabel({ row }: { row: UniverseRow }) {
  return (
    <Stack gap={2}>
      <Group gap={6} wrap="wrap">
        <Text size="sm" fw={500}>
          {row.name ?? row.secid}
        </Text>
        <Badge size="xs" color={liquidityColor(row.liquidityScore)} variant="light">
          {liquidityLabel(row.liquidityScore)}
        </Badge>
        {row.inPortfolio && (
          <Badge size="xs" color="blue" variant="outline">
            в портфеле
          </Badge>
        )}
        {row.inWatchlist && (
          <Badge size="xs" color="grape" variant="outline">
            в watchlist
          </Badge>
        )}
      </Group>
      <Text size="xs" c="dimmed">
        {formatPercent(row.yieldFraction)} · дюрация {row.durationYears !== null ? formatNumber(row.durationYears, 1) : '—'} лет
      </Text>
    </Stack>
  );
}

/** Один предупреждение what-if — фраза по-русски по WhatIfWarningKind (plan/29 §B.1). */
function warningLabel(warning: WhatIfWarning): string {
  const share = formatSharePercentLocal(warning.sharePercentAfter);
  if (warning.kind === 'NewIssuerAboveThreshold') {
    return `новый эмитент «${warning.issuer}» составит ${share} портфеля`;
  }
  return `доля эмитента «${warning.issuer}» превысит лимит концентрации: ${share}`;
}

/** SharePercent приходит уже в процентах (0-100), не в долях — та же конвенция, что CompositionShare.sharePercent (см. doc-comment бэкенда). */
function formatSharePercentLocal(value: number): string {
  return `${value.toFixed(1)}%`;
}

/**
 * Конструктор портфеля (plan/29 §C) — «куда вложить сумму» становится настраиваемой корзиной
 * вместо чистого жадного алгоритма: сумма + строки бумаг с процентами (поиск по портфелю/
 * watchlist/банку, материализация бумаг банка) → штуки с лотами/НКД/комиссией + метрики корзины +
 * what-if всего портфеля (доходность/дюрация/концентрации до/после). Пресет «Максимум доходности»
 * заполняет корзину результатом старого жадного алгоритма (GET /api/analytics/allocation) как
 * отправную точку — дальше пользователь сам крутит проценты.
 * <para>
 * <b>Конвенция долей/процентов</b> (CLAUDE.md): бэкенд работает в долях (0..1), UI — в процентах
 * (1..100) для читаемости ввода. Конвертация происходит ровно на границе — в <c>handleCalculate</c>
 * (percent/100 перед отправкой) и при заполнении пресета (costRub/amount*100 после получения ответа
 * аллокации). Внутри компонента (state строк, нормализация) всё в процентах.
 * </para>
 */
export function BasketConstructor() {
  const [amountRub, setAmountRub] = useState<number>(15000);
  const [lines, setLines] = useState<BasketDraftLine[]>([]);

  const [searchInput, setSearchInput] = useState('');
  const [debouncedSearch] = useDebouncedValue(searchInput, SEARCH_DEBOUNCE_MS);
  const [searchRows, setSearchRows] = useState<UniverseRow[]>([]);
  const [isSearchLoading, setIsSearchLoading] = useState(true);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [selectedSecid, setSelectedSecid] = useState<string | null>(null);
  const [isAdding, setIsAdding] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  const [isPresetLoading, setIsPresetLoading] = useState(false);
  const [presetError, setPresetError] = useState<string | null>(null);

  const [isCalculating, setIsCalculating] = useState(false);
  const [calculateError, setCalculateError] = useState<string | null>(null);
  const [result, setResult] = useState<BasketResponse | null>(null);

  useEffect(() => {
    let cancelled = false;

    fetchUniverse({ search: debouncedSearch || undefined, sortBy: 'yield', sortDir: 'desc', limit: 10 })
      .then((response) => {
        if (cancelled) return;
        setSearchRows(response.rows);
        setIsSearchLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setSearchError(err instanceof Error ? err.message : 'Не удалось загрузить бумаги');
        setIsSearchLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [debouncedSearch]);

  const handleSearchChange = (value: string) => {
    setSearchInput(value);
    setIsSearchLoading(true);
    setSearchError(null);
  };

  const totalPercent = lines.reduce((sum, l) => sum + l.weightPercent, 0);
  const isTotalOff = lines.length > 0 && Math.abs(totalPercent - 100) > 0.01;

  const handleSelectBond = async (secid: string | null) => {
    setSelectedSecid(null); // Select контролируется в "не выбрано" — строка добавляется сразу, не остаётся висеть в поле поиска.
    setAddError(null);
    if (!secid) return;

    const row = searchRows.find((r) => r.secid === secid);

    setIsAdding(true);
    try {
      const materialized = await postMaterialize(secid);
      if (lines.some((l) => l.instrumentId === materialized.instrumentId)) {
        setAddError('Эта бумага уже есть в корзине');
        setIsAdding(false);
        return;
      }
      setLines((prev) => [
        ...prev,
        {
          instrumentId: materialized.instrumentId,
          name: materialized.metrics.name ?? row?.name ?? materialized.secid,
          issuer: materialized.metrics.issuer ?? row?.sector ?? null,
          weightPercent: 0,
        },
      ]);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : 'Не удалось добавить бумагу в корзину');
    } finally {
      setIsAdding(false);
    }
  };

  const handleRemoveLine = (instrumentId: number) => {
    setLines((prev) => prev.filter((l) => l.instrumentId !== instrumentId));
  };

  const handleWeightChange = (instrumentId: number, value: number) => {
    setLines((prev) => prev.map((l) => (l.instrumentId === instrumentId ? { ...l, weightPercent: value } : l)));
  };

  const handleNormalize = () => {
    if (lines.length === 0 || totalPercent <= 0) return;
    setLines((prev) => prev.map((l) => ({ ...l, weightPercent: (l.weightPercent / totalPercent) * 100 })));
  };

  /**
   * Пресет «Максимум доходности» (plan/29 §C.2) — заполняет корзину результатом жадного алгоритма
   * (GET /api/analytics/allocation, тот же вызов, что раньше показывался напрямую в этой секции)
   * как отправную точку: вес каждой строки = её фактическая доля потраченной суммы (estimatedCostRub
   * / amountRub × 100), НЕ включает скипнутые бумаги (allocation.skipped). Дальше пользователь сам
   * подстраивает проценты — сам алгоритм по-прежнему живёт в GET /allocation без изменений контракта.
   */
  const handleUseGreedyPreset = async () => {
    setIsPresetLoading(true);
    setPresetError(null);
    try {
      const allocation = await fetchAllocation(amountRub);
      const newLines: BasketDraftLine[] = allocation.allocations.map((a) => ({
        instrumentId: a.instrumentId,
        name: a.name ?? a.issuer ?? `Инструмент #${a.instrumentId}`,
        issuer: a.issuer,
        weightPercent: amountRub > 0 ? (a.estimatedCostRub / amountRub) * 100 : 0,
      }));
      setLines(newLines);
      setResult(null);
    } catch (err) {
      setPresetError(err instanceof Error ? err.message : 'Не удалось применить пресет');
    } finally {
      setIsPresetLoading(false);
    }
  };

  const handleCalculate = async () => {
    if (lines.length === 0) return;
    setIsCalculating(true);
    setCalculateError(null);
    try {
      const response = await postBasket({
        amountRub,
        // Граница UI↔API: проценты (1-100) → доли (0-1], см. doc-comment компонента.
        lines: lines.map((l) => ({ instrumentId: l.instrumentId, weightFraction: l.weightPercent / 100 })),
      });
      setResult(response);
    } catch (err) {
      setCalculateError(err instanceof Error ? err.message : 'Не удалось рассчитать корзину');
    } finally {
      setIsCalculating(false);
    }
  };

  const searchSelectData = searchRows.map((row) => ({ value: row.secid, label: row.name ?? row.secid }));

  return (
    <Paper withBorder p="md" radius="md" data-testid="basket-constructor">
      <Title order={4} mb="sm">
        Куда вложить сумму — конструктор портфеля
      </Title>
      <Text size="xs" c="dimmed" mb="sm">
        Соберите корзину бумаг процентами от суммы — увидите штуки к покупке и как изменится весь
        портфель (доходность, дюрация, концентрация по эмитентам).
      </Text>

      <Group align="flex-end" mb="sm" wrap="wrap">
        <NumberInput
          label="Сумма, ₽"
          min={1}
          value={amountRub}
          onChange={(v) => setAmountRub(typeof v === 'number' ? v : Number(v) || 0)}
          data-testid="basket-amount-input"
          w={200}
        />
        <Button
          variant="light"
          onClick={handleUseGreedyPreset}
          loading={isPresetLoading}
          data-testid="basket-preset-button"
        >
          Пресет «Максимум доходности»
        </Button>
      </Group>

      {presetError && (
        <Alert color="red" mb="sm" data-testid="basket-preset-error">
          {presetError}
        </Alert>
      )}

      <Select
        label="Добавить бумагу в корзину"
        placeholder="Поиск по имени/ISIN — портфель, watchlist и весь банк"
        searchable
        clearable
        data={searchSelectData}
        value={selectedSecid}
        searchValue={searchInput}
        onSearchChange={handleSearchChange}
        onChange={handleSelectBond}
        filter={({ options }) => options}
        rightSection={isSearchLoading || isAdding ? <Loader size="xs" /> : undefined}
        renderOption={({ option }) => {
          const row = searchRows.find((r) => r.secid === option.value);
          return row ? <UniverseOptionLabel row={row} /> : option.label;
        }}
        nothingFoundMessage={isSearchLoading ? 'Загрузка…' : 'Ничего не найдено'}
        data-testid="basket-search-select"
        mb="sm"
      />

      {searchError && (
        <Alert color="red" mb="sm" data-testid="basket-search-error">
          {searchError}
        </Alert>
      )}
      {addError && (
        <Alert color="red" mb="sm" data-testid="basket-add-error">
          {addError}
        </Alert>
      )}

      {lines.length > 0 && (
        <Stack gap="xs" mb="sm" data-testid="basket-lines">
          <Table.ScrollContainer minWidth={500}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Бумага</Table.Th>
                  <Table.Th>%</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {lines.map((line) => (
                  <Table.Tr key={line.instrumentId} data-testid={`basket-draft-line-${line.instrumentId}`}>
                    <Table.Td>{line.name}</Table.Td>
                    <Table.Td>
                      <NumberInput
                        min={1}
                        max={100}
                        value={line.weightPercent}
                        onChange={(v) => handleWeightChange(line.instrumentId, typeof v === 'number' ? v : Number(v) || 0)}
                        data-testid={`basket-draft-weight-${line.instrumentId}`}
                        w={100}
                      />
                    </Table.Td>
                    <Table.Td>
                      <ActionIcon
                        color="red"
                        variant="subtle"
                        onClick={() => handleRemoveLine(line.instrumentId)}
                        aria-label="Удалить строку"
                        data-testid={`basket-draft-remove-${line.instrumentId}`}
                      >
                        ×
                      </ActionIcon>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          <Group justify="space-between">
            <Text size="sm" fw={600} c={isTotalOff ? 'red' : undefined} data-testid="basket-total-percent">
              Σ = {totalPercent.toFixed(1)}%{isTotalOff && ' (должно быть 100%)'}
            </Text>
            <Button variant="subtle" size="xs" onClick={handleNormalize} data-testid="basket-normalize-button">
              Нормализовать до 100%
            </Button>
          </Group>
        </Stack>
      )}

      <Button
        onClick={handleCalculate}
        loading={isCalculating}
        disabled={lines.length === 0}
        data-testid="basket-calculate-button"
        mb="sm"
      >
        Рассчитать
      </Button>

      {isCalculating && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isCalculating && calculateError && (
        <Alert color="red" data-testid="basket-calculate-error">
          {calculateError}
        </Alert>
      )}

      {!isCalculating && !calculateError && result && (
        <Stack gap="sm" data-testid="basket-result">
          <Table.ScrollContainer minWidth={600}>
            <Table striped highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Бумага</Table.Th>
                  <Table.Th>Штук</Table.Th>
                  <Table.Th>Стоимость</Table.Th>
                  <Table.Th>Вес факт. / целевой</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {result.basket.lines.map((line) => (
                  <Table.Tr key={line.instrumentId} data-testid={`basket-result-line-${line.instrumentId}`}>
                    <Table.Td>{line.name ?? line.issuer ?? `Инструмент #${line.instrumentId}`}</Table.Td>
                    <Table.Td>{line.quantity}</Table.Td>
                    <Table.Td>
                      {formatRub(line.actualCostRub)}
                      <Text size="xs" c="dimmed">
                        цена {formatRub(line.cleanCostRub)} + НКД {formatRub(line.accruedCostRub)} + комиссия{' '}
                        {formatRub(line.commissionCostRub)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {(line.actualWeightFraction * 100).toFixed(1)}% / {(line.targetWeightFraction * 100).toFixed(1)}%
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          <Text size="sm" fw={600} data-testid="basket-leftover">
            Останется нераспределено: {formatRub(result.basket.leftoverRub)}
          </Text>

          <Paper withBorder p="sm" radius="md" data-testid="basket-metrics">
            <Text size="sm" fw={600}>
              Метрики корзины
            </Text>
            <Text size="sm">
              Доходность {formatPercent(result.basket.metrics.weightedYield)} · дюрация{' '}
              {result.basket.metrics.weightedDuration !== null
                ? `${formatNumber(result.basket.metrics.weightedDuration, 2)} лет`
                : '—'}
            </Text>
            {result.basket.metrics.hasExcludedFloaters && (
              <Text size="xs" c="dimmed">
                * доходность считается без флоатеров/индексируемых бумаг — их купонная доходность
                несравнима с YTM.
              </Text>
            )}
          </Paper>

          <Paper withBorder p="sm" radius="md" data-testid="basket-whatif">
            <Text size="sm" fw={600} mb={4}>
              Портфель после покупки
            </Text>
            <Text size="sm">
              Стоимость: {formatRub(result.whatIf.before.totalValueRub)} → {formatRub(result.whatIf.after.totalValueRub)}
            </Text>
            <Text size="sm">
              Доходность: {formatPercent(result.whatIf.before.weightedYield)} →{' '}
              {formatPercent(result.whatIf.after.weightedYield)}
            </Text>
            <Text size="sm">
              Дюрация:{' '}
              {result.whatIf.before.weightedDuration !== null
                ? formatNumber(result.whatIf.before.weightedDuration, 2)
                : '—'}{' '}
              →{' '}
              {result.whatIf.after.weightedDuration !== null
                ? formatNumber(result.whatIf.after.weightedDuration, 2)
                : '—'}{' '}
              лет
            </Text>

            {result.whatIf.warnings.length > 0 && (
              <Stack gap={2} mt="xs" data-testid="basket-whatif-warnings">
                {result.whatIf.warnings.map((w, idx) => (
                  <Text key={`${w.kind}-${w.issuer}-${idx}`} size="sm" c="orange" data-testid={`basket-whatif-warning-${idx}`}>
                    ⚠️ {warningLabel(w)}
                  </Text>
                ))}
              </Stack>
            )}
          </Paper>

          <Disclaimer text={result.disclaimer} />
        </Stack>
      )}
    </Paper>
  );
}
