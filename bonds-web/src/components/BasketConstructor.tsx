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
  SegmentedControl,
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
import { ChartExplainIcon } from './charts/ChartCard';
import { RiskSignalBadges, RiskSignalsCaption } from './RiskSignalBadges';
import type { AllocationLine, AllocationSource, BasketResponse, RiskSignals, UniverseRow, WhatIfWarning } from '../api/types';

const SEARCH_DEBOUNCE_MS = 300;

/**
 * Задача 35 review (MAJOR 2): верхний предел числа строк пула кандидатов пресета, которые
 * последовательно материализуются HTTP-запросами к MOEX (source=market/recommended). Пул
 * кандидатов ограничен бэкендом до AnalyticsEndpoints.MarketAllocationCandidatePoolLimit=200
 * строк (1 лот на secid, у market нет секторного лимита) — без верхнего предела здесь цикл
 * `await postMaterialize` по всем строкам пула мог зависать на минуты последовательных HTTP-
 * вызовов без прогресса (крупная сумма, ~200 тыс. ₽, при лотах ~1000 ₽ — почти весь пул).
 * 25 — компромисс: последовательная материализация 25 бумаг занимает разумное время (секунды —
 * первые десятки секунд) и покрывает типичные суммы пресета; излишек пула отсекается по
 * фактическому весу строки (estimatedCostRub) — оставляем крупнейшие покупки.
 */
const PRESET_MATERIALIZE_LIMIT = 25;

const SOURCE_OPTIONS: { label: string; value: AllocationSource }[] = [
  { label: 'Весь рынок — доходные', value: 'market' },
  { label: 'Рекомендованные', value: 'recommended' },
  { label: 'Мой портфель', value: 'portfolio' },
];

// Задача 35 review (MAJOR 1): банк облигаций (bond_universe) не хранит эмитента — источники
// market/recommended диверсифицируются по грубой классификации сектора (задача 34), НЕ по
// эмитенту. Текст ниже описывает фактическое поведение, не желаемое.
const SOURCE_EXPLANATION =
  'Весь рынок — самые доходные фикс-купонные бумаги биржи (банк облигаций), без дополнительных ' +
  'фильтров, кроме гигиенических (низкая ликвидность/некотировальный список отсеяны). ' +
  'Рекомендованные — та же вселенная, но дополнительно отфильтрована по риск-сигналам (спред/' +
  'ликвидность) и диверсифицирована по секторам (грубая классификация: гособлигации / ' +
  'муниципальные / корпоративные — эмитента биржевая статистика не хранит). Мой портфель — ' +
  'докупка только тех бумаг, что уже есть у вас (прежнее поведение пресета).';

/** Одна строка корзины, собираемая пользователем (проценты — UI-конвенция, конвертируются в доли на границе с API, см. doc-comment handleCalculate). */
interface BasketDraftLine {
  instrumentId: number;
  name: string;
  issuer: string | null;
  weightPercent: number;
  /** Задача 35: SECID банк-кандидата, из которого материализована строка пресетом source=market/
   * recommended — undefined для строк портфеля/ручного добавления. Ключ рендера рыночных строк
   * (см. эскалацию задачи 34 в plan/35) — избегает коллизий с instrumentId ручных строк. */
  secid?: string;
  /** Задача 35: риск-сигналы строки (эхо AllocationLine.riskSignals) — только для source=market/
   * recommended, undefined/null для остальных. */
  riskSignals?: RiskSignals | null;
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

  // Задача 35 §C.1: источник кандидатов пресета «Максимум доходности» — дефолт "market" (весь
  // рынок доходных), пользователь может вернуться к прежнему поведению ("portfolio").
  const [source, setSource] = useState<AllocationSource>('market');

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
  const [presetDisclaimer, setPresetDisclaimer] = useState<string | null>(null);
  // Задача 35 review (MAJOR 2): прогресс последовательной материализации пресета (source=market/
  // recommended) — null вне батча материализации (source=portfolio не материализует построчно).
  const [presetProgress, setPresetProgress] = useState<{ current: number; total: number } | null>(null);
  // Задача 35 review (MAJOR 2): предупреждение (не ошибка) о том, что пул кандидатов больше
  // PRESET_MATERIALIZE_LIMIT — показаны только крупнейшие по весу строки.
  const [presetCapWarning, setPresetCapWarning] = useState<string | null>(null);

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
   * Пресет «Максимум доходности» (plan/29 §C.2, задача 35 §C.1 — источник кандидатов): заполняет
   * корзину результатом жадного алгоритма (GET /api/analytics/allocation?source=) как отправную
   * точку: вес каждой строки = её фактическая доля потраченной суммы (estimatedCostRub / amountRub
   * × 100), НЕ включает скипнутые бумаги (allocation.skipped). Дальше пользователь сам подстраивает
   * проценты — сам алгоритм по-прежнему живёт в GET /allocation без изменений контракта.
   * <para>
   * <b>Подводный камень (эскалация задачи 34, решено здесь):</b> для source=portfolio каждая строка
   * несёт реальный <c>instrumentId</c> (как раньше) — используем напрямую. Для source=market/
   * recommended бэкенд отдаёт <c>instrumentId: null</c> (банк-кандидат не связан с таблицей
   * Instrument) — <c>POST /basket</c> требует реальный Instrument.Id, поэтому каждую такую строку
   * материализуем через <see cref="postMaterialize"/> по её <c>secid</c> (тот же путь, что ручное
   * добавление строки ниже/MarketComparator.handleSelect) ПЕРЕД тем, как класть в корзину.
   * `isPresetLoading` остаётся true на всё время материализации — кнопка пресета показывает
   * загрузку. Ошибка материализации ОДНОЙ бумаги не роняет весь пресет — строка пропускается,
   * `presetError` сообщает, сколько бумаг пропущено.
   * </para>
   * <para>
   * <b>Задача 35 review (MAJOR 2):</b> пул кандидатов source=market/recommended может доходить до
   * бэкендового потолка (200 строк) — последовательная материализация всех строк HTTP-запросами к
   * MOEX могла зависать на минуты без обратной связи. Материализуем не более
   * {@link PRESET_MATERIALIZE_LIMIT} строк — крупнейшие по фактическому весу покупки
   * (<c>estimatedCostRub</c>), остальные молча отбрасываются с предупреждением
   * (`presetCapWarning`, не ошибка). На время батча `presetProgress` даёт счётчик «X из Y» вместо
   * голого спиннера.
   * </para>
   */
  const handleUseGreedyPreset = async () => {
    setIsPresetLoading(true);
    setPresetError(null);
    setPresetDisclaimer(null);
    setPresetCapWarning(null);
    setPresetProgress(null);
    try {
      const allocation = await fetchAllocation(amountRub, { source });
      setPresetDisclaimer(allocation.disclaimer || null);

      if (source === 'portfolio') {
        const newLines: BasketDraftLine[] = allocation.allocations
          .filter((a): a is AllocationLine & { instrumentId: number } => a.instrumentId !== null)
          .map((a) => ({
            instrumentId: a.instrumentId,
            name: a.name ?? a.issuer ?? `Инструмент #${a.instrumentId}`,
            issuer: a.issuer,
            weightPercent: amountRub > 0 ? (a.estimatedCostRub / amountRub) * 100 : 0,
            secid: a.secid ?? undefined,
            riskSignals: a.riskSignals,
          }));
        setLines(newLines);
        setResult(null);
        return;
      }

      // source=market/recommended: instrumentId всегда null — материализуем каждую строку по её
      // secid, последовательно. Пул кандидатов ограничен потолком бэкенда (200 строк) — капим до
      // PRESET_MATERIALIZE_LIMIT крупнейших по estimatedCostRub (см. doc-comment выше/MAJOR 2).
      const eligible = allocation.allocations.filter(
        (a): a is AllocationLine & { secid: string } => a.secid !== null, // контракт задачи 34: market/recommended всегда несут secid.
      );
      const capped = [...eligible].sort((a, b) => b.estimatedCostRub - a.estimatedCostRub).slice(0, PRESET_MATERIALIZE_LIMIT);

      setPresetProgress({ current: 0, total: capped.length });
      const materializedLines: BasketDraftLine[] = [];
      let failedCount = 0;
      for (let i = 0; i < capped.length; i++) {
        const a = capped[i];
        try {
          const materialized = await postMaterialize(a.secid);
          materializedLines.push({
            instrumentId: materialized.instrumentId,
            name: a.name ?? materialized.metrics.name ?? a.secid,
            issuer: a.issuer ?? materialized.metrics.issuer ?? null,
            weightPercent: amountRub > 0 ? (a.estimatedCostRub / amountRub) * 100 : 0,
            secid: a.secid,
            riskSignals: a.riskSignals,
          });
        } catch {
          failedCount += 1;
        } finally {
          setPresetProgress({ current: i + 1, total: capped.length });
        }
      }
      setLines(materializedLines);
      setResult(null);
      if (failedCount > 0) {
        setPresetError(`Не удалось добавить ${failedCount} бумаг(и) из пресета — пропущены.`);
      }
      if (eligible.length > capped.length) {
        setPresetCapWarning(
          `Показаны ${capped.length} крупнейших строк пресета из ${eligible.length} — остальные пропущены.`,
        );
      }
    } catch (err) {
      setPresetError(err instanceof Error ? err.message : 'Не удалось применить пресет');
    } finally {
      setIsPresetLoading(false);
      setPresetProgress(null);
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
          disabled={isPresetLoading}
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

      {/* Задача 35 review (MAJOR MINOR/контролы): на время батча материализации пресета сумма и
          источник кандидатов заблокированы — пользователь не может менять параметры под ногами у
          идущего последовательного цикла запросов к MOEX (см. handleUseGreedyPreset). */}
      {isPresetLoading && presetProgress && (
        <Text size="xs" c="dimmed" mb="sm" data-testid="basket-preset-progress">
          Материализация {presetProgress.current} из {presetProgress.total}…
        </Text>
      )}

      <Group gap={6} align="center" mb="sm">
        <Text size="xs" fw={600} c="dimmed">
          Источник кандидатов пресета
        </Text>
        <ChartExplainIcon
          title="Источник кандидатов"
          explanation={SOURCE_EXPLANATION}
          data-testid="basket-source-explain-icon"
        />
      </Group>
      <SegmentedControl
        value={source}
        onChange={(v) => setSource(v as AllocationSource)}
        data={SOURCE_OPTIONS}
        disabled={isPresetLoading}
        mb="sm"
        data-testid="basket-source-control"
      />

      {presetError && (
        <Alert color="red" mb="sm" data-testid="basket-preset-error">
          {presetError}
        </Alert>
      )}

      {!presetError && presetCapWarning && (
        <Alert color="yellow" mb="sm" data-testid="basket-preset-cap-warning">
          {presetCapWarning}
        </Alert>
      )}

      {!presetError && presetDisclaimer && (
        <Text size="xs" c="dimmed" mb="sm" data-testid="basket-preset-disclaimer">
          {presetDisclaimer}
        </Text>
      )}

      {lines.some((l) => l.riskSignals) && <RiskSignalsCaption />}

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
                {lines.map((line) => {
                  // Задача 35 §C.1/пресет: ключ рендера рыночных строк (source=market/recommended) —
                  // secid, не instrumentId (см. doc-comment handleUseGreedyPreset) — ручные строки
                  // secid не несут, откатываются на instrumentId (прежнее поведение, тесты не меняются).
                  const rowKey = line.secid ?? line.instrumentId;
                  return (
                    <Table.Tr key={rowKey} data-testid={`basket-draft-line-${rowKey}`}>
                      <Table.Td>
                        <Text size="sm">{line.name}</Text>
                        {line.riskSignals && <RiskSignalBadges signals={line.riskSignals} testIdSuffix={`draft-${rowKey}`} />}
                      </Table.Td>
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
                  );
                })}
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
