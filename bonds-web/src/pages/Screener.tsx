import { useEffect, useState } from 'react';
import {
  Title,
  Stack,
  Table,
  Badge,
  Alert,
  Loader,
  Center,
  Text,
  UnstyledButton,
  Group,
  Button,
  Tooltip,
  TextInput,
  NumberInput,
  Select,
  Switch,
  Paper,
  Collapse,
  Pagination,
} from '@mantine/core';
import { useDebouncedValue, useDisclosure, useMediaQuery } from '@mantine/hooks';
import { ChartExplainIcon } from '../components/charts/ChartCard';
import { Disclaimer } from '../components/Disclaimer';
import { postWatchlistItem } from '../api/watchlist';
import {
  useScreenerStore,
  SCREENER_PAGE_SIZE,
  DEFAULT_SCREENER_FILTERS,
  type ScreenerSortBy,
} from '../store/useScreenerStore';
import { liquidityLabel, liquidityColor, hiddenReasonLabel } from '../utils/universeDisplay';
import { formatPercent, formatNumber, formatBp, formatRubCompact, formatDate, formatDateTime } from '../utils/format';
import type { UniverseRow } from '../api/types';

const GSPREAD_TOOLTIP =
  'Оценка по данным MOEX относительно кривой ОФЗ — не точный расчёт движка (доступен для ' +
  'бумаг из портфеля/watchlist после материализации).';

const PAGE_EXPLANATION = (
  <>
    Скринер — весь банк облигаций MOEX (не только ваш портфель/watchlist), обновляется раз в час.
    «Скрытые» — бумаги, которые гигиенический фильтр по умолчанию убирает из выдачи (низкий
    оборот, некотировальный список, аномальная доходность, нет данных дюрации/цены, близость к
    погашению) — их можно посмотреть переключателем «показать скрытые», каждая отмечена причиной.
    Данные MOEX, это не инвестиционная рекомендация.
  </>
);

const SECTOR_OPTIONS = [
  { value: 'Гособлигации', label: 'Гособлигации' },
  { value: 'Муниципальные', label: 'Муниципальные' },
  { value: 'Корпоративные', label: 'Корпоративные' },
];

function maturityOrOfferLabel(row: UniverseRow): string {
  if (row.offerDate) return `${formatDate(row.offerDate)} (оферта)`;
  if (row.maturityDate) return formatDate(row.maturityDate);
  return '—';
}

/** Бейджи «в портфеле»/«в watchlist» — переиспользуются десктопной таблицей и мобильными карточками. */
function OwnershipBadges({ row }: { row: UniverseRow }) {
  return (
    <>
      {row.inPortfolio && (
        <Badge size="xs" color="blue" variant="outline" data-testid={`screener-in-portfolio-${row.secid}`}>
          в портфеле
        </Badge>
      )}
      {row.inWatchlist && (
        <Badge size="xs" color="grape" variant="outline" data-testid={`screener-in-watchlist-${row.secid}`}>
          в watchlist
        </Badge>
      )}
    </>
  );
}

/** Кнопка «+ watchlist» — POST /api/watchlist по ISIN, оптимистичное обновление бейджа (plan/28 часть A.3). */
function AddToWatchlistButton({ row }: { row: UniverseRow }) {
  const markInWatchlist = useScreenerStore((s) => s.markInWatchlist);
  const [isAdding, setIsAdding] = useState(false);

  if (!row.isin) return null;
  if (row.inWatchlist) {
    return (
      <Text size="xs" c="dimmed">
        уже в watchlist
      </Text>
    );
  }

  const handleAdd = async () => {
    setIsAdding(true);
    try {
      await postWatchlistItem({ isin: row.isin! });
      markInWatchlist(row.isin!);
    } catch {
      // Молча игнорируем (например, уже в watchlist на сервере) — та же обработка, что MarketComparator.
    } finally {
      setIsAdding(false);
    }
  };

  return (
    <Button
      size="xs"
      variant="light"
      loading={isAdding}
      onClick={handleAdd}
      data-testid={`screener-add-watchlist-${row.secid}`}
    >
      + watchlist
    </Button>
  );
}

/** Панель серверных фильтров — общая для десктопа (открыта) и мобилы (в Collapse, plan/28 часть B). */
function FilterPanel() {
  const filters = useScreenerStore((s) => s.filters);
  const setFilters = useScreenerStore((s) => s.setFilters);
  const resetFilters = useScreenerStore((s) => s.resetFilters);

  const [searchInput, setSearchInput] = useState(filters.search);
  const [debouncedSearch] = useDebouncedValue(searchInput, 300);

  useEffect(() => {
    if (debouncedSearch !== filters.search) {
      setFilters({ search: debouncedSearch });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debouncedSearch]);

  const isDefault =
    filters.search === '' &&
    filters.minYield === null &&
    filters.maxYield === null &&
    filters.minDurationYears === null &&
    filters.maxDurationYears === null &&
    filters.sector === null &&
    !filters.includeHidden;

  return (
    <Stack gap="sm" data-testid="screener-filter-panel">
      <TextInput
        label="Поиск"
        placeholder="Имя / ISIN / SECID"
        value={searchInput}
        onChange={(e) => setSearchInput(e.currentTarget.value)}
        data-testid="screener-search-input"
      />
      <Group grow>
        <NumberInput
          label="YTM от, %"
          value={filters.minYield !== null ? filters.minYield * 100 : ''}
          onChange={(v) => setFilters({ minYield: v === '' ? null : Number(v) / 100 })}
          data-testid="screener-min-yield"
        />
        <NumberInput
          label="YTM до, %"
          value={filters.maxYield !== null ? filters.maxYield * 100 : ''}
          onChange={(v) => setFilters({ maxYield: v === '' ? null : Number(v) / 100 })}
          data-testid="screener-max-yield"
        />
      </Group>
      <Group grow>
        <NumberInput
          label="Дюрация от, лет"
          value={filters.minDurationYears ?? ''}
          onChange={(v) => setFilters({ minDurationYears: v === '' ? null : Number(v) })}
          data-testid="screener-min-duration"
        />
        <NumberInput
          label="Дюрация до, лет"
          value={filters.maxDurationYears ?? ''}
          onChange={(v) => setFilters({ maxDurationYears: v === '' ? null : Number(v) })}
          data-testid="screener-max-duration"
        />
      </Group>
      <Select
        label="Сектор"
        placeholder="Все секторы"
        data={SECTOR_OPTIONS}
        value={filters.sector}
        onChange={(v) => setFilters({ sector: v })}
        clearable
        data-testid="screener-sector-select"
      />
      <Switch
        label="Показать скрытые (гигиенический фильтр)"
        checked={filters.includeHidden}
        onChange={(e) => setFilters({ includeHidden: e.currentTarget.checked })}
        data-testid="screener-include-hidden-toggle"
      />
      {!isDefault && (
        <Button
          variant="subtle"
          size="xs"
          w="fit-content"
          onClick={() => {
            setSearchInput(DEFAULT_SCREENER_FILTERS.search);
            resetFilters();
          }}
          data-testid="screener-reset-filters"
        >
          Сбросить фильтры
        </Button>
      )}
    </Stack>
  );
}

/** Строка статуса банка — GET /api/universe/status + hiddenCount из последнего ответа таблицы (plan/28 часть A.3.1). */
function StatusBar() {
  const status = useScreenerStore((s) => s.status);
  const total = useScreenerStore((s) => s.total);
  const hiddenCount = useScreenerStore((s) => s.hiddenCount);

  if (!status) return null;

  return (
    <Text size="sm" c="dimmed" data-testid="screener-status-bar">
      В банке {status.totalBonds} бумаг · скрыто {hiddenCount || status.hiddenBonds} (неликвид/листинг/аномальная
      доходность) · обновлено {status.lastRefreshUtc ? formatDateTime(status.lastRefreshUtc) : '—'}
      {total !== status.totalBonds && (
        <Text span c="dimmed">
          {' '}
          · в текущей выдаче {total}
        </Text>
      )}
    </Text>
  );
}

/** Мобильная карточка строки скринера (паттерн PositionCard, plan/28 часть B). */
function ScreenerCard({ row }: { row: UniverseRow }) {
  const [expanded, { toggle }] = useDisclosure(false);

  return (
    <Paper
      withBorder
      p="sm"
      radius="md"
      style={row.isHidden ? { opacity: 0.6 } : undefined}
      data-testid={`screener-card-${row.secid}`}
    >
      <Stack gap={4}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Stack gap={0}>
            <Text fw={600} size="sm" lineClamp={2}>
              {row.name ?? row.secid}
            </Text>
            <Text size="xs" c="dimmed">
              {row.sector ?? '—'}
            </Text>
          </Stack>
          <Text fw={700} size="sm">
            {formatPercent(row.yieldFraction)}
          </Text>
        </Group>

        <Group gap={4} wrap="wrap">
          <Badge size="xs" color={liquidityColor(row.liquidityScore)} variant="light">
            {liquidityLabel(row.liquidityScore)}
          </Badge>
          {row.isHidden && (
            <Badge size="xs" color="gray" variant="filled" data-testid={`screener-hidden-badge-${row.secid}`}>
              скрыта: {hiddenReasonLabel(row.hiddenReason)}
            </Badge>
          )}
          <OwnershipBadges row={row} />
        </Group>

        <Group justify="space-between" mt={4}>
          <Stack gap={0}>
            <Text size="xs" c="dimmed">
              Дюрация, лет
            </Text>
            <Text size="sm">{row.durationYears !== null ? formatNumber(row.durationYears, 1) : '—'}</Text>
          </Stack>
          <Stack gap={0} align="center">
            <Text size="xs" c="dimmed">
              Цена, %
            </Text>
            <Text size="sm">{row.pricePercent !== null ? formatNumber(row.pricePercent, 2) : '—'}</Text>
          </Stack>
          <Stack gap={0} align="flex-end">
            <Text size="xs" c="dimmed">
              Оборот/день
            </Text>
            <Text size="sm">{formatRubCompact(row.turnoverRub)}</Text>
          </Stack>
        </Group>

        <UnstyledButton onClick={toggle} data-testid={`screener-card-toggle-${row.secid}`}>
          <Text size="xs" c="violet">
            {expanded ? 'Скрыть детали ▲' : 'Подробнее ▼'}
          </Text>
        </UnstyledButton>

        <Collapse expanded={expanded}>
          <Stack gap={4} data-testid={`screener-card-details-${row.secid}`}>
            <Group justify="space-between">
              <Text size="xs" c="dimmed">
                G-спред (прибл.), б.п.
              </Text>
              <Text size="xs">{formatBp(row.gspreadApproxFraction)}</Text>
            </Group>
            <Group justify="space-between">
              <Text size="xs" c="dimmed">
                Погашение/оферта
              </Text>
              <Text size="xs">{maturityOrOfferLabel(row)}</Text>
            </Group>
            <Group justify="space-between">
              <Text size="xs" c="dimmed">
                ISIN
              </Text>
              <Text size="xs">{row.isin ?? '—'}</Text>
            </Group>
          </Stack>
        </Collapse>

        <Group mt={4}>
          <AddToWatchlistButton row={row} />
        </Group>
      </Stack>
    </Paper>
  );
}

function SortableHeader({ sortKey, label }: { sortKey: ScreenerSortBy; label: string }) {
  const sortBy = useScreenerStore((s) => s.sortBy);
  const sortDir = useScreenerStore((s) => s.sortDir);
  const toggleSort = useScreenerStore((s) => s.toggleSort);

  return (
    <UnstyledButton onClick={() => toggleSort(sortKey)} data-testid={`screener-sort-${sortKey}`}>
      <Group gap={4} wrap="nowrap">
        <Text fw={700} size="sm">
          {label}
        </Text>
        {sortBy === sortKey && (
          <Text size="xs" c="dimmed">
            {sortDir === 'asc' ? '↑' : '↓'}
          </Text>
        )}
      </Group>
    </UnstyledButton>
  );
}

/**
 * Страница «Скринер» (plan/28) — весь банк облигаций MOEX таблицей: серверные фильтры/поиск/
 * сортировка/пагинация по 50 (GET /api/universe, задача 26), статусная строка (GET
 * /api/universe/status), метки «в портфеле»/«в watchlist», действие «+ watchlist». Мобильная
 * версия — карточки (паттерн PositionCard из задачи 21) с накопительной пагинацией «показать
 * ещё». Сравнение с рынком уже живёт в MarketComparator на «Рекомендациях» — здесь намеренно нет
 * второй точки входа в сравнение (YAGNI, см. plan/28).
 */
export function Screener() {
  const { rows, total, disclaimer, isLoading, error, offset, load, loadStatus, setOffset } = useScreenerStore();
  const isMobile = useMediaQuery('(max-width: 48em)');
  const [filtersOpened, { toggle: toggleFilters }] = useDisclosure(false);

  // Мобильная накопительная пагинация «показать ещё» — независима от offset стора (который
  // используется десктопной постраничной Pagination); держим локально видимый лимит карточек.
  const [mobileVisibleCount, setMobileVisibleCount] = useState(SCREENER_PAGE_SIZE);

  useEffect(() => {
    load();
    loadStatus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const currentPage = Math.floor(offset / SCREENER_PAGE_SIZE) + 1;
  const totalPages = Math.max(1, Math.ceil(total / SCREENER_PAGE_SIZE));

  const visibleRows = isMobile ? rows.slice(0, mobileVisibleCount) : rows;

  return (
    <Stack gap="md">
      <Group gap={6}>
        <Title order={2}>Скринер</Title>
        <ChartExplainIcon title="Скринер" explanation={PAGE_EXPLANATION} data-testid="screener-explain-icon" />
      </Group>

      <StatusBar />

      {isMobile ? (
        <Stack gap="xs">
          <Button variant="light" size="xs" onClick={toggleFilters} data-testid="screener-filters-toggle">
            {filtersOpened ? 'Скрыть фильтры ▲' : 'Фильтры ▼'}
          </Button>
          <Collapse expanded={filtersOpened}>
            <FilterPanel />
          </Collapse>
        </Stack>
      ) : (
        <Paper withBorder p="md" radius="md">
          <FilterPanel />
        </Paper>
      )}

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить банк облигаций" data-testid="screener-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && rows.length === 0 && (
        <Alert color="gray" data-testid="screener-empty">
          Ничего не найдено — попробуйте изменить фильтры или включить «показать скрытые».
        </Alert>
      )}

      {!isLoading && !error && rows.length > 0 && (
        <>
          {isMobile ? (
            <Stack gap="sm" data-testid="screener-cards">
              {visibleRows.map((row) => (
                <ScreenerCard key={row.secid} row={row} />
              ))}
              {mobileVisibleCount < rows.length && (
                <Button
                  variant="light"
                  onClick={() => setMobileVisibleCount((c) => c + SCREENER_PAGE_SIZE)}
                  data-testid="screener-show-more"
                >
                  Показать ещё
                </Button>
              )}
            </Stack>
          ) : (
            <>
              <Table.ScrollContainer minWidth={1100}>
                <Table striped highlightOnHover data-testid="screener-table">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Бумага</Table.Th>
                      <Table.Th>Сектор</Table.Th>
                      <Table.Th>
                        <SortableHeader sortKey="yield" label="YTM" />
                      </Table.Th>
                      <Table.Th>
                        <SortableHeader sortKey="duration" label="Дюрация, лет" />
                      </Table.Th>
                      <Table.Th>Цена, %</Table.Th>
                      <Table.Th>
                        <Group gap={4} wrap="nowrap">
                          <SortableHeader sortKey="gspread" label="G-спред (прибл.), б.п." />
                          <Tooltip label={GSPREAD_TOOLTIP} multiline w={280} withArrow>
                            <Text size="xs" c="dimmed" span>
                              ⓘ
                            </Text>
                          </Tooltip>
                        </Group>
                      </Table.Th>
                      <Table.Th>
                        <SortableHeader sortKey="turnover" label="Оборот/день" />
                      </Table.Th>
                      <Table.Th>Ликвидность</Table.Th>
                      <Table.Th>Погашение/оферта</Table.Th>
                      <Table.Th>Действия</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {rows.map((row) => (
                      <Table.Tr
                        key={row.secid}
                        data-testid={`screener-row-${row.secid}`}
                        style={row.isHidden ? { opacity: 0.6 } : undefined}
                      >
                        <Table.Td>
                          <Tooltip label={row.isin ?? '—'} withArrow>
                            <Stack gap={2}>
                              <Text size="sm">{row.name ?? row.secid}</Text>
                              <Group gap={4} wrap="wrap">
                                <OwnershipBadges row={row} />
                                {row.isHidden && (
                                  <Badge
                                    size="xs"
                                    color="gray"
                                    variant="filled"
                                    data-testid={`screener-hidden-badge-${row.secid}`}
                                  >
                                    скрыта: {hiddenReasonLabel(row.hiddenReason)}
                                  </Badge>
                                )}
                              </Group>
                            </Stack>
                          </Tooltip>
                        </Table.Td>
                        <Table.Td>{row.sector ?? '—'}</Table.Td>
                        <Table.Td>{formatPercent(row.yieldFraction)}</Table.Td>
                        <Table.Td>{row.durationYears !== null ? formatNumber(row.durationYears, 1) : '—'}</Table.Td>
                        <Table.Td>{row.pricePercent !== null ? formatNumber(row.pricePercent, 2) : '—'}</Table.Td>
                        <Table.Td>{formatBp(row.gspreadApproxFraction)}</Table.Td>
                        <Table.Td>{formatRubCompact(row.turnoverRub)}</Table.Td>
                        <Table.Td>
                          <Badge size="sm" color={liquidityColor(row.liquidityScore)} variant="light">
                            {liquidityLabel(row.liquidityScore)}
                          </Badge>
                        </Table.Td>
                        <Table.Td>{maturityOrOfferLabel(row)}</Table.Td>
                        <Table.Td>
                          <AddToWatchlistButton row={row} />
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>

              <Group justify="space-between">
                <Text size="xs" c="dimmed">
                  {total} бумаг · страница {currentPage} из {totalPages}
                </Text>
                <Pagination
                  value={currentPage}
                  total={totalPages}
                  onChange={(page) => setOffset((page - 1) * SCREENER_PAGE_SIZE)}
                  data-testid="screener-pagination"
                />
              </Group>
            </>
          )}
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Stack>
  );
}
