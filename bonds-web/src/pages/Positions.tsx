import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { EmptyState, ErrorState, Page, Section } from '@mvv42/kit';
import {
  Stack,
  Table,
  Badge,
  Loader,
  Text,
  UnstyledButton,
  Group,
  Button,
  Tooltip,
  ActionIcon,
  Select,
  Paper,
  SegmentedControl,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { usePositionsStore } from '../store/usePositionsStore';
import { useFundsStore } from '../store/useFundsStore';
import { useCurrenciesStore } from '../store/useCurrenciesStore';
import { useFreedomStore } from '../store/useFreedomStore';
import { useCredentialsStore } from '../store/useCredentialsStore';
import { useSettingsStore } from '../store/useSettingsStore';
import { useRelativeValueStore } from '../store/useRelativeValueStore';
import { usePositionVerdictStore } from '../store/usePositionVerdictStore';
import { useLiveQuotes } from '../hooks/useLiveQuotes';
import { Disclaimer } from '../components/Disclaimer';
import { FundsTable } from '../components/FundsTable';
import { CurrenciesTable } from '../components/CurrenciesTable';
import { FreedomTable } from '../components/FreedomTable';
import { FREEDOM_EMPTY_DESCRIPTION, FREEDOM_EMPTY_TITLE, FREEDOM_FX_NOTE } from '../components/freedomCopy';
import { CURRENCY_FX_DISCLAIMER } from '../components/currenciesCopy';
import { FUND_KEY_RATE_NOTE } from '../components/fundsCopy';
import { PortfolioIntradayChart } from '../components/PortfolioIntradayChart';
import { PositionCard } from '../components/PositionCard';
import { LiveMarketValueCell } from '../components/positionsShared';
import { CompactRelativeValueIndicator } from '../components/RelativeValueBadge';
import { PositionVerdictBadge } from '../components/PositionVerdictBadge';
import {
  effectiveYield,
  COUPON_TYPE_LABEL,
  YIELD_TOOLTIP_TEXT,
  DIRTY_PRICE_TOOLTIP_TEXT,
  type SortKey,
  type SortState,
} from '../utils/positionsDisplay';
import type { PositionRow } from '../api/types';
import { formatRub, formatDaysUntil, formatPercent, formatNumber, formatBp } from '../utils/format';
import { computePositionsTotals } from '../utils/positionsAggregation';
import { buildYieldHeatmapScale } from '../utils/yieldHeatmap';

type PositionsTab = 'bonds' | 'funds' | 'currency' | 'freedom';

/**
 * Экран «Позиции»: вкладки «Облигации | Фонды | Валюта | Freedom» (задачи 53, 56, 60). Вкладка живёт в URL (`?tab=funds|currency|freedom`),
 * по умолчанию — облигации: существующая таблица без изменений. Фонды и валюта — отдельные домены, в облигационные
 * виджеты не подмешиваются. Вкладка «Валюта» показывается, только если `/api/currencies` вернул хотя бы одну валюту
 * (включая проданную) — либо если открыта по прямой ссылке. Вкладка «Freedom» — ТОЛЬКО при `capabilities.freedom` (ключи подключены):
 * без неё ни вкладки, ни запроса `/api/brokers/freedom/holdings`, а `?tab=freedom` открывает облигации.
 */
export function Positions() {
  const [params, setParams] = useSearchParams();
  const rawTab = params.get('tab');
  const freedomEnabled = useCredentialsStore((s) => s.capabilities?.freedom === true);
  const tab: PositionsTab =
    rawTab === 'funds' ? 'funds' : rawTab === 'currency' ? 'currency' : rawTab === 'freedom' && freedomEnabled ? 'freedom' : 'bonds';
  const hasCurrencies = useCurrenciesStore((s) => s.currencies.length > 0);
  const loadCurrencies = useCurrenciesStore((s) => s.load);

  useEffect(() => {
    void loadCurrencies();
  }, [loadCurrencies]);

  const data = [
    { value: 'bonds', label: 'Облигации' },
    { value: 'funds', label: 'Фонды' },
    ...(hasCurrencies || tab === 'currency' ? [{ value: 'currency', label: 'Валюта' }] : []),
    ...(freedomEnabled ? [{ value: 'freedom', label: 'Freedom' }] : []),
  ];

  const tabs = (
    <SegmentedControl
      size="sm"
      value={tab}
      onChange={(v) => {
        const next = new URLSearchParams(params);
        if (v === 'funds' || v === 'currency' || v === 'freedom') next.set('tab', v);
        else next.delete('tab');
        setParams(next, { replace: true });
      }}
      data={data}
      data-testid="positions-tab-toggle"
    />
  );

  if (tab === 'currency') return <CurrencyPositionsView tabs={tabs} />;
  if (tab === 'freedom') return <FreedomPositionsView tabs={tabs} />;
  return tab === 'funds' ? <FundsPositionsView tabs={tabs} /> : <BondsPositionsView tabs={tabs} />;
}

/** Вкладка «Валюта»: юани и т.п. — курс, стоимость, курсовая разница (GET /api/currencies, данные грузит родитель). */
function CurrencyPositionsView({ tabs }: { tabs: ReactNode }) {
  const { currencies, totals, disclaimer, loaded, isLoading, error } = useCurrenciesStore();

  return (
    <Page title="Позиции" description="Валюта на счёте: курс, стоимость и курсовая разница" width="wide">
      {tabs}

      {(isLoading || !loaded) && !error && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить валюту" error={error} data-testid="currencies-error" />
        </Section>
      )}

      {!isLoading && loaded && !error && currencies.length === 0 && (
        <Section>
          <EmptyState
            data-testid="currencies-empty"
            title="Валюты нет"
            description="Валюта появится после синка, если куплена в Т-Инвестициях."
          />
        </Section>
      )}

      {!isLoading && !error && currencies.length > 0 && (
        <>
          <p className="bonds-note" data-testid="currencies-fx-note">
            {CURRENCY_FX_DISCLAIMER}
          </p>
          <CurrenciesTable currencies={currencies} totals={totals} />
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Page>
  );
}

/** Вкладка «Freedom»: позиции и деньги счёта Freedom (GET /api/brokers/freedom/holdings), суммы в валюте бумаги + рубли по курсам bonds. */
function FreedomPositionsView({ tabs }: { tabs: ReactNode }) {
  const { holdings, loaded, isLoading, error, load } = useFreedomStore();

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Page title="Позиции" description="Счёт Freedom: ETF и акции в валюте бумаги, оценка в рублях" width="wide">
      {tabs}

      {(isLoading || !loaded) && !error && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить счёт Freedom" error={error} data-testid="freedom-error" />
        </Section>
      )}

      {!isLoading && loaded && !error && holdings && holdings.positions.length === 0 && holdings.cash.length === 0 && (
        <Section>
          <EmptyState data-testid="freedom-empty" title={FREEDOM_EMPTY_TITLE} description={FREEDOM_EMPTY_DESCRIPTION} />
        </Section>
      )}

      {!isLoading && !error && holdings && (holdings.positions.length > 0 || holdings.cash.length > 0) && (
        <>
          <p className="bonds-note" data-testid="freedom-fx-note">
            {FREEDOM_FX_NOTE}
          </p>
          <FreedomTable holdings={holdings} />
        </>
      )}

      <Disclaimer text={holdings?.disclaimer} />
    </Page>
  );
}

/** Вкладка «Фонды»: список паёв с P&L, фактической доходностью и оценкой (GET /api/funds). */
function FundsPositionsView({ tabs }: { tabs: ReactNode }) {
  const { funds, totals, disclaimer, isLoading, error, load } = useFundsStore();

  useEffect(() => {
    load();
  }, [load]);

  return (
    <Page title="Позиции" description="Паи фондов (БПИФ): стоимость, P&L, выплаты и оценка доходности" width="wide">
      {tabs}

      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить фонды" error={error} data-testid="funds-error" />
        </Section>
      )}

      {!isLoading && !error && funds.length === 0 && (
        <Section>
          <EmptyState
            data-testid="funds-empty"
            title="Фондов нет"
            description="Фондов нет — они появятся после синка, если куплены в Т-Инвестициях."
          />
        </Section>
      )}

      {!isLoading && !error && funds.length > 0 && (
        <>
          <p className="bonds-note" data-testid="funds-key-rate-note">
            {FUND_KEY_RATE_NOTE}
          </p>
          <FundsTable funds={funds} totals={totals} />
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Page>
  );
}

/**
 * Вкладка «Облигации» — таблица позиций с расчётными метриками (
 * цена входа/P&L — plan/14).
 * Графика/календарь/сигналы — 09b/09c.
 */
function BondsPositionsView({ tabs }: { tabs: ReactNode }) {
  const { positions, disclaimer, isLoading, error, load } = usePositionsStore();
  const { settings, load: loadSettings } = useSettingsStore();
  const relativeValueByPosition = useRelativeValueStore((s) => s.positionsById);
  const loadRelativeValue = useRelativeValueStore((s) => s.load);
  const verdictsByPosition = usePositionVerdictStore((s) => s.verdictsById);
  const loadVerdicts = usePositionVerdictStore((s) => s.load);
  const navigate = useNavigate();
  const [sort, setSort] = useState<SortState>({ key: 'yield', direction: 'desc' });
  // < 768px — карточки вместо таблицы (10+ колонок нечитаемы на телефоне).
  const isMobile = useMediaQuery('(max-width: 48em)');

  // Поллинг живых цен (только видимая вкладка + грубо торговые часы на клиенте) —
  // пишет в useLiveStore, откуда его читают LiveMarketValueCell ниже и PortfolioIntradayChart.
  useLiveQuotes();

  useEffect(() => {
    load();
    loadSettings();
    // RV-индикатор грузится ленивым ОДНИМ запросом (не блокируя таблицу
    // позиций) — useRelativeValueStore.load() сам не даёт себе выполниться дважды и молча
    // проглатывает ошибку (индикатор просто не покажется, см. doc-comment стора).
    loadRelativeValue();
    // Вердикты позиций (задача 49) — тоже ленивый независимый запрос: отказ не роняет таблицу, бейдж просто не рисуется.
    loadVerdicts();
  }, [load, loadSettings, loadRelativeValue, loadVerdicts]);

  const sorted = useMemo(() => {
    const copy = [...positions];
    const selector = sort.key === 'yield' ? effectiveYield : (row: PositionRow) => row.unrealizedPnlPercent;
    copy.sort((a, b) => {
      const av = selector(a);
      const bv = selector(b);
      if (av === null && bv === null) return 0;
      if (av === null) return 1;
      if (bv === null) return -1;
      return sort.direction === 'asc' ? av - bv : bv - av;
    });
    return copy;
  }, [positions, sort]);

  const toggleSort = (key: SortKey) =>
    setSort((s) => (s.key === key ? { key, direction: s.direction === 'asc' ? 'desc' : 'asc' } : { key, direction: 'desc' }));

  // Строка «Итого» — суммарная стоимость и средневзвешенные доходность/дюрация.
  const totals = useMemo(() => computePositionsTotals(positions), [positions]);

  // heatmap колонки «Доходность» — перцентиль считается только по сравнимым
  // доходностям (обычные бумаги, effectiveYield !== null для floater/indexed возвращает
  // currentYield, но красить его heatmap'ом некорректно — несравнимо с YTM обычных бумаг).
  const yieldHeatmapScale = useMemo(() => {
    const comparableYields = positions
      .filter((p) => !p.isFloater && !p.isIndexed && p.ytmEffective !== null)
      .map((p) => p.ytmEffective as number);
    return buildYieldHeatmapScale(comparableYields);
  }, [positions]);

  return (
    <Page title="Позиции" description="Бумаги портфеля: стоимость, P&L, доходность и риск" width="wide">
      {tabs}

      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить позиции" error={error} data-testid="positions-error" />
        </Section>
      )}

      {!isLoading && !error && positions.length === 0 && settings && !settings.tInvestTokenConfigured && (
        <Section>
          <EmptyState
            data-testid="positions-empty-no-token"
            title="Нужно подключить брокерский счёт"
            description="Чтобы увидеть позиции, укажите токен T-Invest (только на чтение) в настройках — он хранится в зашифрованном виде и используется для синхронизации портфеля."
            action={
              <Button onClick={() => navigate('/settings')} data-testid="goto-settings-button">
                Перейти в настройки
              </Button>
            }
          />
        </Section>
      )}

      {!isLoading && !error && positions.length === 0 && settings?.tInvestTokenConfigured && (
        <Section>
          <EmptyState
            data-testid="positions-empty"
            title="Портфель пуст"
            description="Токен подключён. Позиции появятся здесь после синхронизации с брокерским счётом — нажмите «Обновить данные» в шапке или подождите следующего автоматического цикла."
          />
        </Section>
      )}

      {!isLoading && !error && positions.length > 0 && (
        <>
          <PortfolioIntradayChart />

          <p className="bonds-note">
            Более низкая доходность не означает «хуже» без учёта срока до погашения и риска
            эмитента — сравнивайте бумаги с одинаковым горизонтом и качеством.
          </p>

          {isMobile ? (
            <Stack gap="sm" data-testid="positions-cards">
              <Select
                label="Сортировка"
                data={[
                  { value: 'yield-desc', label: 'Доходность ↓' },
                  { value: 'yield-asc', label: 'Доходность ↑' },
                  { value: 'pnl-desc', label: 'P&L ↓' },
                  { value: 'pnl-asc', label: 'P&L ↑' },
                ]}
                value={`${sort.key}-${sort.direction}`}
                onChange={(value) => {
                  if (!value) return;
                  const [key, direction] = value.split('-') as [SortKey, 'asc' | 'desc'];
                  setSort({ key, direction });
                }}
                data-testid="mobile-sort-select"
                allowDeselect={false}
              />

              <Paper withBorder p="sm" radius="md" data-testid="positions-totals-mobile">
                <Text size="xs" c="dimmed">
                  Итого по портфелю
                </Text>
                <Text fw={700}>{formatRub(totals.totalMarketValueRub)}</Text>
                {totals.totalAccruedRub > 0 && (
                  <Text size="xs" c="dimmed" data-testid="accrued-caption-totals-mobile">
                    в т.ч. НКД {formatRub(totals.totalAccruedRub)}
                  </Text>
                )}
                <Group gap="md" mt={4}>
                  <Text size="xs" c="dimmed">
                    Ср.взв. доходность: <Text span fw={600}>{formatPercent(totals.weightedYield)}</Text>
                    {totals.hasExcludedFloaters && '*'}
                  </Text>
                  <Text size="xs" c="dimmed">
                    Ср.взв. дюрация: <Text span fw={600}>{formatNumber(totals.weightedDuration)}</Text>
                  </Text>
                </Group>
                {totals.hasExcludedFloaters && (
                  <Text size="xs" c="dimmed" mt={2}>
                    * без флоатеров/индексируемых — их доходность несравнима с YTM
                  </Text>
                )}
              </Paper>

              {sorted.map((row) => (
                <PositionCard
                  key={row.positionId}
                  row={row}
                  verdict={verdictsByPosition[row.positionId]}
                  onClick={() => navigate(`/positions/${row.positionId}`)}
                />
              ))}
            </Stack>
          ) : (
          <Section padding="none" title="Бумаги в портфеле" description={`${positions.length} ${positions.length === 1 ? 'позиция' : positions.length < 5 ? 'позиции' : 'позиций'} · строка открывает карточку`}>
          <Table.ScrollContainer minWidth={1100}>
            <Table highlightOnHover data-testid="positions-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Бумага</Table.Th>
                  <Table.Th>Эмитент</Table.Th>
                  <Table.Th>Кол-во</Table.Th>
                  <Table.Th>
                    <Group gap={4} wrap="nowrap">
                      <Text fw={700} size="sm">
                        Рыночная стоимость
                      </Text>
                      <Tooltip label={DIRTY_PRICE_TOOLTIP_TEXT} multiline w={280} withArrow>
                        <ActionIcon
                          variant="subtle"
                          color="gray"
                          size="xs"
                          radius="xl"
                          data-testid="dirty-price-info-icon"
                          aria-label="Пояснение к грязным ценам"
                        >
                          <Text size="xs" fw={700}>
                            ?
                          </Text>
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Table.Th>
                  <Table.Th>Ср. цена входа</Table.Th>
                  <Table.Th>
                    <UnstyledButton onClick={() => toggleSort('pnl')} data-testid="sort-pnl">
                      <Group gap={4} wrap="nowrap">
                        <Text fw={700} size="sm">
                          P&amp;L
                        </Text>
                        {sort.key === 'pnl' && (
                          <Text size="xs" c="dimmed">
                            {sort.direction === 'asc' ? '↑' : '↓'}
                          </Text>
                        )}
                      </Group>
                    </UnstyledButton>
                  </Table.Th>
                  <Table.Th>Купоны получено</Table.Th>
                  <Table.Th>
                    <Group gap={4} wrap="nowrap">
                      <UnstyledButton onClick={() => toggleSort('yield')} data-testid="sort-yield">
                        <Group gap={4} wrap="nowrap">
                          <Text fw={700} size="sm">
                            Доходность
                          </Text>
                          {sort.key === 'yield' && (
                            <Text size="xs" c="dimmed">
                              {sort.direction === 'asc' ? '↑' : '↓'}
                            </Text>
                          )}
                        </Group>
                      </UnstyledButton>
                      <Tooltip label={YIELD_TOOLTIP_TEXT} multiline w={280} withArrow>
                        <ActionIcon
                          variant="subtle"
                          color="gray"
                          size="xs"
                          radius="xl"
                          data-testid="yield-info-icon"
                          aria-label="Пояснение к доходности"
                        >
                          <Text size="xs" fw={700}>
                            ?
                          </Text>
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Table.Th>
                  <Table.Th>Дюрация, лет</Table.Th>
                  <Table.Th>G-спред, б.п.</Table.Th>
                  <Table.Th>До погашения/оферты</Table.Th>
                  <Table.Th>Вердикт</Table.Th>
                  <Table.Th>Пометки</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {sorted.map((row) => {
                  const yieldValue = effectiveYield(row);
                  const isCurrentYield = row.isFloater || row.isIndexed;
                  const pnlColor =
                    row.unrealizedPnlRub === null ? undefined : row.unrealizedPnlRub >= 0 ? 'green' : 'red';
                  const totalReturnLabel =
                    row.totalReturnPercent === null
                      ? 'Полная доходность (P&L + купоны) недоступна — журнал операций неполон.'
                      : `Полная доходность (P&L + купоны): ${formatPercent(row.totalReturnPercent)}`;
                  return (
                    <Table.Tr
                      key={row.positionId}
                      data-testid={`position-row-${row.positionId}`}
                      onClick={() => navigate(`/positions/${row.positionId}`)}
                      style={{ cursor: 'pointer' }}
                    >
                      <Table.Td>{row.name ?? row.issuer ?? row.isin ?? '—'}</Table.Td>
                      <Table.Td>{row.issuer ?? '—'}</Table.Td>
                      <Table.Td>{row.quantity}</Table.Td>
                      <Table.Td>
                        <LiveMarketValueCell staticValueRub={row.marketValueRub} positionId={row.positionId} />
                        {row.accruedTotalRub > 0 && (
                          <Text size="xs" c="dimmed" data-testid={`accrued-caption-${row.positionId}`}>
                            в т.ч. НКД {formatRub(row.accruedTotalRub)}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>{formatRub(row.averageCostRub)}</Table.Td>
                      <Table.Td>
                        <Tooltip label={totalReturnLabel} withArrow>
                          <Stack gap={0}>
                            <Group gap={4} wrap="nowrap">
                              {row.unrealizedPnlRub !== null && (
                                <Text c={pnlColor} size="sm" span aria-hidden="true">
                                  {row.unrealizedPnlRub >= 0 ? '▲' : '▼'}
                                </Text>
                              )}
                              <Text c={pnlColor} fw={500}>
                                {formatRub(row.unrealizedPnlRub)}
                              </Text>
                            </Group>
                            <Text c={pnlColor} size="xs">
                              {formatPercent(row.unrealizedPnlPercent)}
                            </Text>
                          </Stack>
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>{formatRub(row.couponsReceivedRub)}</Table.Td>
                      <Table.Td
                        style={{
                          backgroundColor: isCurrentYield || yieldValue === null ? undefined : yieldHeatmapScale(yieldValue),
                        }}
                        data-testid={`yield-cell-${row.positionId}`}
                      >
                        <Group gap={4} wrap="nowrap">
                          <Text>{formatPercent(yieldValue)}</Text>
                          {isCurrentYield && (
                            <Text size="xs" c="dimmed" title="Текущая доходность, не YTM">
                              (тек.)
                            </Text>
                          )}
                        </Group>
                      </Table.Td>
                      <Table.Td>{formatNumber(row.modifiedDuration)}</Table.Td>
                      <Table.Td>{formatBp(row.gSpread)}</Table.Td>
                      <Table.Td>
                        {formatDaysUntil(row.calculatedToOffer ? row.horizonDate : row.maturityDate)}{row.calculatedToOffer ? ' (оферта)' : ''}
                      </Table.Td>
                      <Table.Td>
                        <PositionVerdictBadge verdict={verdictsByPosition[row.positionId]} />
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4} wrap="wrap">
                          <Tooltip label={`Сектор: ${row.sector ?? '—'}. Тип купона: ${COUPON_TYPE_LABEL[row.couponType]}.`} withArrow>
                            <Badge size="sm" color="gray" variant="outline">
                              {row.sector ?? COUPON_TYPE_LABEL[row.couponType]}
                            </Badge>
                          </Tooltip>
                          {row.isFloater && (
                            <Badge size="sm" color="blue" variant="light">
                              плавающая
                            </Badge>
                          )}
                          {row.isIndexed && (
                            <Badge size="sm" color="teal" variant="light">
                              индексируемая
                            </Badge>
                          )}
                          {row.calculatedToOffer && (
                            <Badge size="sm" color="orange" variant="light">
                              к оферте
                            </Badge>
                          )}
                          {row.dataIncomplete && (
                            <Badge size="sm" color="red" variant="light">
                              данные неполные
                            </Badge>
                          )}
                          {row.costBasisIncomplete && (
                            <Badge size="sm" color="gray" variant="light" data-testid={`cost-basis-incomplete-${row.positionId}`}>
                              журнал неполон
                            </Badge>
                          )}
                          {row.isOutOfScopeCurrency && (
                            <Badge size="sm" color="grape" variant="light">
                              валютная / вне скоупа
                            </Badge>
                          )}
                          <CompactRelativeValueIndicator rv={relativeValueByPosition[row.positionId]} />
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
              <Table.Tfoot>
                <Table.Tr data-testid="positions-totals-row">
                  <Table.Th>Итого</Table.Th>
                  <Table.Th />
                  <Table.Th />
                  <Table.Th>
                    {formatRub(totals.totalMarketValueRub)}
                    {totals.totalAccruedRub > 0 && (
                      <Text size="xs" c="dimmed" fw={400} data-testid="accrued-caption-totals">
                        в т.ч. НКД {formatRub(totals.totalAccruedRub)}
                      </Text>
                    )}
                  </Table.Th>
                  <Table.Th />
                  <Table.Th />
                  <Table.Th />
                  <Table.Th>
                    {formatPercent(totals.weightedYield)}
                    {totals.hasExcludedFloaters && '*'}
                  </Table.Th>
                  <Table.Th>{formatNumber(totals.weightedDuration)}</Table.Th>
                  <Table.Th />
                  <Table.Th />
                  <Table.Th />
                  <Table.Th />
                </Table.Tr>
                {totals.hasExcludedFloaters && (
                  <Table.Tr>
                    <Table.Th colSpan={12} fw={400}>
                      <Text size="xs" c="dimmed">
                        * средневзвешенная доходность без флоатеров/индексируемых — их доходность несравнима с YTM
                      </Text>
                    </Table.Th>
                  </Table.Tr>
                )}
              </Table.Tfoot>
            </Table>
          </Table.ScrollContainer>
          </Section>
          )}
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Page>
  );
}
