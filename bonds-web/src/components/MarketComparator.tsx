import { useEffect, useState } from 'react';
import { Select, Group, Text, Badge, Alert, Loader, Center, Button, Stack, Paper } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { fetchUniverse, postMaterialize } from '../api/universe';
import { postReplacement } from '../api/recommendations';
import { postWatchlistItem } from '../api/watchlist';
import { ReplacementBreakdown } from './ReplacementBreakdown';
import { formatPercent, formatNumber, formatHorizon } from '../utils/format';
import type { UniverseRow, MaterializeResponse, ReplacementResponse } from '../api/types';

const SEARCH_DEBOUNCE_MS = 300;
const REPLACEMENT_HORIZON_YEARS = 2;

/** Русская подпись бейджа ликвидности (LiquidityScore, задача 26). */
function liquidityLabel(score: UniverseRow['liquidityScore']): string {
  switch (score) {
    case 'High':
      return 'высокая ликвидность';
    case 'Medium':
      return 'средняя ликвидность';
    case 'Low':
      return 'низкая ликвидность';
    default:
      return 'ликвидность неизвестна';
  }
}

function liquidityColor(score: UniverseRow['liquidityScore']): string {
  switch (score) {
    case 'High':
      return 'teal';
    case 'Medium':
      return 'yellow';
    case 'Low':
      return 'red';
    default:
      return 'gray';
  }
}

/** Одна опция выпадашки — карточка с именем/YTM/дюрацией/бейджами (renderOption, plan/27 §B.1). */
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

/**
 * Задача 27 часть B — выпадашка-сравнивалка «слабая позиция vs весь рынок»: Combobox (Select с
 * серверным поиском) → выбор бумаги банка → materialize (заводит Instrument+котировку) →
 * POST /api/analytics/replacement (targetInstrumentId) → карточка «держать vs переложиться» с той
 * же формулой-разбивкой, что матрица замен (см. ReplacementBreakdown).
 * <para>
 * Пусто в поиске → топ-10 самых доходных (гигиенические фильтры банка исключают скрытые бумаги —
 * дефолт GET /api/universe); ввод текста → тот же запрос с search= (debounce ~300мс).
 * </para>
 */
export function MarketComparator({ holdPositionId }: { holdPositionId: number }) {
  const [searchInput, setSearchInput] = useState('');
  const [debouncedSearch] = useDebouncedValue(searchInput, SEARCH_DEBOUNCE_MS);

  const [rows, setRows] = useState<UniverseRow[]>([]);
  // Дефолт true — компонент всегда стартует с загрузки топ-10 (пустой поиск), см. эффект ниже.
  const [isSearchLoading, setIsSearchLoading] = useState(true);
  const [searchError, setSearchError] = useState<string | null>(null);

  const [selectedSecid, setSelectedSecid] = useState<string | null>(null);

  const [isCompareLoading, setIsCompareLoading] = useState(false);
  const [compareError, setCompareError] = useState<string | null>(null);
  const [materialized, setMaterialized] = useState<MaterializeResponse | null>(null);
  const [replacement, setReplacement] = useState<ReplacementResponse | null>(null);

  const [isAddingToWatchlist, setIsAddingToWatchlist] = useState(false);
  const [addedToWatchlist, setAddedToWatchlist] = useState(false);

  useEffect(() => {
    let cancelled = false;

    // setState здесь — не "синхронный setState в теле эффекта" (react-hooks/set-state-in-effect):
    // это подписка на внешнюю асинхронную операцию (fetch), setState вызывается только в её
    // callback'ах (then/catch), тот же паттерн, что PortfolioIntradayChart/PositionDetail.
    // "Начало загрузки" сигнализируется синхронно из onSearchChange (реальный обработчик события),
    // не отсюда — см. handleSearchChange ниже.
    fetchUniverse({ search: debouncedSearch || undefined, sortBy: 'yield', sortDir: 'desc', limit: 10 })
      .then((response) => {
        if (cancelled) return;
        setRows(response.rows);
        setIsSearchLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setSearchError(err instanceof Error ? err.message : 'Не удалось загрузить бумаги рынка');
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

  const selectedRow = rows.find((r) => r.secid === selectedSecid) ?? null;

  const handleSelect = async (secid: string | null) => {
    setSelectedSecid(secid);
    setMaterialized(null);
    setReplacement(null);
    setCompareError(null);
    setAddedToWatchlist(false);
    if (!secid) return;

    setIsCompareLoading(true);
    try {
      const materializeResult = await postMaterialize(secid);
      setMaterialized(materializeResult);

      const replacementResult = await postReplacement({
        holdPositionId,
        targetInstrumentId: materializeResult.instrumentId,
        horizonYears: REPLACEMENT_HORIZON_YEARS,
      });
      setReplacement(replacementResult);
      setIsCompareLoading(false);
    } catch (err) {
      setCompareError(err instanceof Error ? err.message : 'Не удалось сравнить с рынком');
      setIsCompareLoading(false);
    }
  };

  const handleAddToWatchlist = async () => {
    if (!materialized) return;
    setIsAddingToWatchlist(true);
    try {
      await postWatchlistItem({ isin: materialized.isin });
      setAddedToWatchlist(true);
    } catch {
      // Молча игнорируем (например, уже в watchlist) — кнопка не критична для сравнения.
    } finally {
      setIsAddingToWatchlist(false);
    }
  };

  const selectData = rows.map((row) => ({ value: row.secid, label: row.name ?? row.secid }));
  const lowLiquidity = materialized?.metrics && selectedRow?.liquidityScore === 'Low';

  return (
    <Stack gap="sm" data-testid={`market-comparator-${holdPositionId}`}>
      <Select
        label="Сравнить с рынком"
        placeholder="Поиск по имени/ISIN — пусто = топ-10 доходных"
        searchable
        clearable
        data={selectData}
        value={selectedSecid}
        searchValue={searchInput}
        onSearchChange={handleSearchChange}
        onChange={handleSelect}
        filter={({ options }) => options}
        rightSection={isSearchLoading ? <Loader size="xs" /> : undefined}
        renderOption={({ option }) => {
          const row = rows.find((r) => r.secid === option.value);
          return row ? <UniverseOptionLabel row={row} /> : option.label;
        }}
        nothingFoundMessage={isSearchLoading ? 'Загрузка…' : 'Ничего не найдено'}
        data-testid={`market-comparator-select-${holdPositionId}`}
      />

      {searchError && (
        <Alert color="red" data-testid={`market-comparator-search-error-${holdPositionId}`}>
          {searchError}
        </Alert>
      )}

      {isCompareLoading && (
        <Center py="sm" data-testid={`market-comparator-loading-${holdPositionId}`}>
          <Loader size="sm" />
        </Center>
      )}

      {!isCompareLoading && compareError && (
        <Alert color="red" data-testid={`market-comparator-error-${holdPositionId}`}>
          {compareError}
        </Alert>
      )}

      {!isCompareLoading && !compareError && materialized && replacement && (
        <Paper withBorder p="sm" radius="md" data-testid={`market-comparator-result-${holdPositionId}`}>
          <Text fw={600} size="sm">
            {materialized.metrics.name ?? materialized.metrics.issuer ?? materialized.secid}
          </Text>
          <Text size="sm" c="teal" fw={600} data-testid={`market-comparator-benefit-${holdPositionId}`}>
            выгода{replacement.netBenefitAfterTaxRub !== null ? ' после налога' : ''} ≈{' '}
            {(replacement.netBenefitAfterTaxRub ?? replacement.netBenefitRub).toLocaleString('ru-RU')} ₽
            {replacement.annualizedBenefitFraction !== null && (
              <> (~{formatPercent(replacement.annualizedBenefitFraction)} годовых)</>
            )}{' '}
            за {formatHorizon(replacement.horizonYears)}
          </Text>

          {lowLiquidity && (
            <Alert color="orange" mt="xs" data-testid={`market-comparator-liquidity-warning-${holdPositionId}`}>
              Низкая ликвидность бумаги — фактическая цена исполнения сделки может заметно отличаться
              от последней биржевой котировки MOEX
              {selectedRow?.slippageEstimateFraction !== null && selectedRow?.slippageEstimateFraction !== undefined && (
                <> (оценка проскальзывания ≈ {formatPercent(selectedRow.slippageEstimateFraction)})</>
              )}
              .
            </Alert>
          )}

          <ReplacementBreakdown
            data={{
              spreadFraction: replacement.spreadFraction,
              capitalRub: replacement.capitalRub,
              horizonYears: replacement.horizonYears,
              grossGainRub: replacement.grossGainRub,
              sellCommissionRub: replacement.sellCommissionRub,
              buyCommissionRub: replacement.buyCommissionRub,
              netBenefitRub: replacement.netBenefitRub,
              annualizedBenefitFraction: replacement.annualizedBenefitFraction,
              // ReplacementResponse несёт раздельные ставки продажи/покупки (обычно совпадают —
              // единый резолвер применяет одну ставку к обеим сделкам); формула-разбивка (как у
              // матрицы) показывает одну ставку — берём ставку продажи как представительную.
              commissionRateUsed: replacement.sellCommissionRateUsed,
              commissionRateSource: replacement.commissionRateSource,
              sellTaxEstimateRub: replacement.sellTaxEstimateRub,
              netBenefitAfterTaxRub: replacement.netBenefitAfterTaxRub,
            }}
            testIdSuffix={`market-${holdPositionId}`}
          />

          <Text size="xs" c="dimmed" mt="xs">
            Сравнение использует данные MOEX — ликвидность и реальная цена исполнения могут отличаться
            от расчёта.
          </Text>

          <Button
            mt="sm"
            size="xs"
            variant="light"
            onClick={handleAddToWatchlist}
            loading={isAddingToWatchlist}
            disabled={addedToWatchlist}
            data-testid={`market-comparator-watchlist-button-${holdPositionId}`}
          >
            {addedToWatchlist ? 'добавлено в watchlist' : 'В watchlist'}
          </Button>
        </Paper>
      )}
    </Stack>
  );
}
