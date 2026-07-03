import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
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
  Table,
  SimpleGrid,
  SegmentedControl,
  Tooltip as MantineTooltip,
  ActionIcon,
  Button,
} from '@mantine/core';
import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ReferenceDot,
  ReferenceLine,
} from 'recharts';
import { fetchPositionDetail } from '../api/positions';
import { Disclaimer } from '../components/Disclaimer';
import { ChartCard, ChartTooltip, CHART_GRID_PROPS, CHART_HEIGHT, CHART_MARGIN } from '../components/charts';
import { pickAxisTicks } from '../components/charts/axisTicks';
import type { PositionDetail as PositionDetailDto, PriceHistoryRange } from '../api/types';
import { formatRub, formatPercent, formatBp, formatNumber, formatDate, formatDaysUntil } from '../utils/format';

const COUPON_TYPE_LABEL: Record<PositionDetailDto['couponType'], string> = {
  Fixed: 'Фиксированный',
  Floating: 'Плавающий',
  Indexed: 'Индексируемый',
};

const OPERATION_TYPE_LABEL: Record<string, string> = {
  Buy: 'Покупка',
  Sell: 'Продажа',
  Coupon: 'Купон',
  Amortization: 'Амортизация',
  Redemption: 'Погашение',
  Tax: 'Налог',
  Fee: 'Комиссия',
};

const RANGE_OPTIONS: { value: PriceHistoryRange; label: string }[] = [
  { value: '1m', label: '1м' },
  { value: '6m', label: '6м' },
  { value: '1y', label: '1г' },
  { value: 'all', label: 'Всё' },
];

interface MetricCardProps {
  label: string;
  value: string;
  explanation: string;
  extra?: string;
}

function MetricCard({ label, value, explanation, extra }: MetricCardProps) {
  return (
    <Paper withBorder p="sm" radius="md" data-testid={`metric-card-${label}`}>
      <Group gap={4} wrap="nowrap" mb={4}>
        <Text size="xs" c="dimmed">
          {label}
        </Text>
        <MantineTooltip label={explanation} multiline w={260} withArrow>
          <ActionIcon variant="subtle" color="gray" size="xs" radius="xl" aria-label={`Пояснение к «${label}»`}>
            <Text size="xs" fw={700}>
              ?
            </Text>
          </ActionIcon>
        </MantineTooltip>
      </Group>
      <Text fw={700} size="lg">
        {value}
      </Text>
      {extra && (
        <Text size="xs" c="dimmed">
          {extra}
        </Text>
      )}
    </Paper>
  );
}

interface DetailFetchState {
  detail: PositionDetailDto | null;
  isLoading: boolean;
  error: string | null;
}

/**
 * Карточка позиции (drill-down, plan/19) — график цены с маркерами сделок, метрики с
 * объяснениями, календарь бумаги (купоны/амортизации/оферты), журнал операций и оценка
 * «если продать сейчас». Открывается кликом по строке таблицы /positions.
 */
export function PositionDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [range, setRange] = useState<PriceHistoryRange>('6m');
  const [state, setState] = useState<DetailFetchState>({ detail: null, isLoading: true, error: null });
  const { detail, isLoading, error } = state;

  useEffect(() => {
    if (!id) return;
    let cancelled = false;

    // setState здесь — не "синхронный setState в теле эффекта" (react-hooks/set-state-in-effect):
    // это подписка на внешнюю асинхронную операцию (fetch), setState вызывается только в её
    // callback'ах (then/catch), тот же паттерн, что PortfolioIntradayChart (plan/16 часть B).
    void fetchPositionDetail(id, range)
      .then((response) => {
        if (!cancelled) setState({ detail: response, isLoading: false, error: null });
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setState({
            detail: null,
            isLoading: false,
            error: err instanceof Error ? err.message : 'Не удалось загрузить карточку позиции',
          });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [id, range]);

  const priceChartData = useMemo(() => {
    if (!detail) return [];
    return detail.priceHistory
      .filter((p) => p.closePricePercent !== null)
      .map((p) => ({
        date: p.date,
        label: formatDate(p.date),
        priceRub: ((p.closePricePercent ?? 0) / 100) * detail.faceValue,
      }));
  }, [detail]);

  const tradeMarkers = useMemo(() => {
    if (!detail) return [];
    return detail.operations
      .filter((op) => op.type === 'Buy' || op.type === 'Sell')
      .map((op) => {
        const dateLabel = op.date.slice(0, 10);
        // Ищем ближайшую по дате точку графика, чтобы маркер лёг на линию (иначе ReferenceDot
        // без совпадающего X-значения просто не отрисуется).
        const match = priceChartData.find((p) => p.date === dateLabel);
        return match ? { ...match, type: op.type } : null;
      })
      .filter((m): m is NonNullable<typeof m> => m !== null);
  }, [detail, priceChartData]);

  if (!id) {
    return (
      <Alert color="red" title="Некорректная ссылка" data-testid="position-detail-invalid-id">
        Не указан идентификатор позиции.
      </Alert>
    );
  }

  return (
    <Stack gap="md" data-testid="position-detail-page">
      <Group justify="space-between">
        <Button variant="subtle" color="gray" onClick={() => navigate('/positions')} data-testid="back-to-positions">
          ← К списку позиций
        </Button>
      </Group>

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить карточку позиции" data-testid="position-detail-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && detail && (
        <>
          {/* ── Шапка ────────────────────────────────────────────────────────────── */}
          <Paper withBorder p="md" radius="md" data-testid="position-detail-header">
            <Group justify="space-between" wrap="wrap">
              <div>
                <Title order={2}>{detail.name ?? detail.issuer ?? detail.isin}</Title>
                <Text size="sm" c="dimmed">
                  {detail.isin} · {detail.issuer ?? '—'}
                  {detail.sector ? ` · ${detail.sector}` : ''}
                </Text>
              </div>
              <Stack gap={0} align="flex-end">
                <Text size="sm" c="dimmed">
                  {detail.quantity} шт × {formatRub(detail.dirtyPrice)}
                </Text>
                <Text fw={700} size="xl">
                  {formatRub(detail.marketValueRub)}
                </Text>
              </Stack>
            </Group>
            <Group gap={4} mt="sm" wrap="wrap">
              <Badge size="sm" color="gray" variant="outline">
                {COUPON_TYPE_LABEL[detail.couponType]}
              </Badge>
              {detail.isFloater && (
                <Badge size="sm" color="blue" variant="light">
                  плавающая
                </Badge>
              )}
              {detail.isIndexed && (
                <Badge size="sm" color="teal" variant="light">
                  индексируемая
                </Badge>
              )}
              {detail.calculatedToOffer && (
                <Badge size="sm" color="orange" variant="light">
                  к оферте
                </Badge>
              )}
              {detail.dataIncomplete && (
                <Badge size="sm" color="red" variant="light">
                  данные неполные
                </Badge>
              )}
              {detail.isOutOfScopeCurrency && (
                <Badge size="sm" color="grape" variant="light">
                  валютная / вне скоупа
                </Badge>
              )}
            </Group>
          </Paper>

          {/* ── График цены ──────────────────────────────────────────────────────── */}
          <ChartCard
            title="Цена"
            data-testid="price-history-chart"
            explanation="Дневная цена бумаги (грязная — с учётом НКД), пересчитанная в рубли по номиналу. Точки — ваши покупки/продажи; пунктир — средняя цена входа, если она известна."
            controls={
              <SegmentedControl
                size="xs"
                value={range}
                onChange={(v) => {
                  setState((prev) => ({ ...prev, isLoading: true }));
                  setRange(v as PriceHistoryRange);
                }}
                data={RANGE_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
                data-testid="price-range-toggle"
              />
            }
          >
            {priceChartData.length === 0 ? (
              <Text size="sm" c="dimmed" data-testid="price-history-empty">
                История цены недоступна за выбранный период.
              </Text>
            ) : (
              <ResponsiveContainer width="100%" height={CHART_HEIGHT}>
                <LineChart data={priceChartData} margin={CHART_MARGIN}>
                  <CartesianGrid {...CHART_GRID_PROPS} />
                  <XAxis dataKey="label" ticks={pickAxisTicks(priceChartData.map((d) => d.label))} />
                  <YAxis tickFormatter={(v: number) => formatRub(v)} width={90} domain={['auto', 'auto']} />
                  <Tooltip
                    content={({ active, payload }) => {
                      if (!active || !payload?.length) return null;
                      const point = payload[0].payload as (typeof priceChartData)[number];
                      return <ChartTooltip title={point.label} rows={[{ label: 'Цена', value: formatRub(point.priceRub) }]} />;
                    }}
                  />
                  <Line type="monotone" dataKey="priceRub" stroke="var(--mantine-color-violet-6)" dot={false} strokeWidth={2} />
                  {detail.averageCostRub !== null && (
                    <ReferenceLine
                      y={detail.averageCostRub}
                      stroke="var(--mantine-color-gray-6)"
                      strokeDasharray="4 4"
                      label={{ value: 'Ср. цена входа', position: 'insideTopLeft', fontSize: 11, fill: 'var(--mantine-color-gray-6)' }}
                    />
                  )}
                  {tradeMarkers.map((marker, idx) => (
                    <ReferenceDot
                      key={`${marker.date}-${idx}`}
                      x={marker.label}
                      y={marker.priceRub}
                      r={5}
                      fill={marker.type === 'Buy' ? 'var(--mantine-color-teal-6)' : 'var(--mantine-color-red-5)'}
                      stroke="none"
                    />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            )}
          </ChartCard>

          {/* ── Метрики ──────────────────────────────────────────────────────────── */}
          <SimpleGrid cols={{ base: 2, sm: 3, md: 6 }} data-testid="position-detail-metrics">
            <MetricCard
              label={detail.isFloater || detail.isIndexed ? 'Текущая доходность' : 'YTM'}
              value={formatPercent(detail.isFloater || detail.isIndexed ? detail.currentYield : detail.ytmEffective)}
              explanation="Эффективная доходность к погашению/оферте от текущей рыночной цены (для флоатеров/индексируемых бумаг — текущая купонная доходность). Не зависит от вашей цены покупки."
            />
            <MetricCard
              label="Текущая доходность"
              value={formatPercent(detail.currentYield)}
              explanation="Годовой купонный доход, делённый на грязную цену. Не учитывает изменение цены бумаги к погашению — только текущий денежный поток."
            />
            <MetricCard
              label="Дюрация (мод.)"
              value={formatNumber(detail.modifiedDuration)}
              explanation="Модифицированная дюрация — на сколько процентов изменится цена бумаги при сдвиге доходности на 1 процентный пункт. Больше значение — выше чувствительность к ставкам."
            />
            <MetricCard
              label="G-спред"
              value={formatBp(detail.gSpread)}
              explanation="Разница между доходностью бумаги и безрисковой кривой ОФЗ на сопоставимый срок. Больше значение — выше премия за кредитный риск эмитента."
            />
            <MetricCard
              label="НКД"
              value={formatRub(detail.accruedInterest)}
              explanation="Накопленный купонный доход — часть следующего купона, накопленная к сегодняшней дате. Входит в грязную цену, которую вы платите при покупке."
            />
            <MetricCard
              label="Следующий купон"
              value={formatDate(detail.couponSchedule.find((c) => !c.isPast)?.couponDate)}
              explanation="Дата ближайшей будущей купонной выплаты по графику бумаги."
              extra={
                detail.couponSchedule.find((c) => !c.isPast)?.valueForPositionRub !== undefined
                  ? formatRub(detail.couponSchedule.find((c) => !c.isPast)?.valueForPositionRub ?? null)
                  : undefined
              }
            />
          </SimpleGrid>

          {/* ── Календарь бумаги ─────────────────────────────────────────────────── */}
          <Paper withBorder p="md" radius="md" data-testid="instrument-calendar">
            <Text fw={600} mb="xs">
              Календарь бумаги
            </Text>
            {detail.couponSchedule.length === 0 && detail.amortizationSchedule.length === 0 && detail.offerSchedule.length === 0 ? (
              <Text size="sm" c="dimmed" data-testid="instrument-calendar-empty">
                График купонов/амортизаций/оферт недоступен.
              </Text>
            ) : (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Дата</Table.Th>
                    <Table.Th>Событие</Table.Th>
                    <Table.Th>Сумма на позицию</Table.Th>
                    <Table.Th>Пометки</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {[
                    ...detail.couponSchedule.map((c) => ({
                      key: `coupon-${c.couponDate}`,
                      date: c.couponDate,
                      label: 'Купон',
                      amount: c.valueForPositionRub,
                      isPast: c.isPast,
                      isKnown: c.isKnown,
                    })),
                    ...detail.amortizationSchedule.map((a) => ({
                      key: `amort-${a.date}`,
                      date: a.date,
                      label: 'Амортизация',
                      amount: a.amountForPositionRub,
                      isPast: a.isPast,
                      isKnown: a.isKnown,
                    })),
                    ...detail.offerSchedule.map((o) => ({
                      key: `offer-${o.date}`,
                      date: o.date,
                      label: o.offerType === 'Put' ? 'Оферта (put)' : 'Оферта (call)',
                      amount: null as number | null,
                      isPast: o.isPast,
                      isKnown: true,
                    })),
                  ]
                    .sort((a, b) => a.date.localeCompare(b.date))
                    .map((row) => (
                      <Table.Tr key={row.key} data-testid={row.isPast ? 'calendar-row-past' : 'calendar-row-future'}>
                        <Table.Td>{formatDate(row.date)}</Table.Td>
                        <Table.Td>{row.label}</Table.Td>
                        <Table.Td>{row.amount !== null ? formatRub(row.amount) : '—'}</Table.Td>
                        <Table.Td>
                          <Group gap={4}>
                            {row.isPast && (
                              <Badge size="xs" color="gray" variant="light">
                                прошло
                              </Badge>
                            )}
                            {!row.isKnown && (
                              <Badge size="xs" color="yellow" variant="light">
                                оценка
                              </Badge>
                            )}
                          </Group>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                </Table.Tbody>
              </Table>
            )}
          </Paper>

          {/* ── Мои операции ─────────────────────────────────────────────────────── */}
          <Paper withBorder p="md" radius="md" data-testid="position-operations">
            <Text fw={600} mb="xs">
              Мои операции
            </Text>
            {detail.operations.length === 0 ? (
              <Text size="sm" c="dimmed" data-testid="position-operations-empty">
                Операций по бумаге в журнале нет.
              </Text>
            ) : (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Дата</Table.Th>
                    <Table.Th>Тип</Table.Th>
                    <Table.Th>Количество</Table.Th>
                    <Table.Th>Сумма</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {detail.operations.map((op) => (
                    <Table.Tr key={op.id}>
                      <Table.Td>{formatDate(op.date)}</Table.Td>
                      <Table.Td>{OPERATION_TYPE_LABEL[op.type] ?? op.type}</Table.Td>
                      <Table.Td>{op.quantity ?? '—'}</Table.Td>
                      <Table.Td c={op.amountRub >= 0 ? 'green' : 'red'}>{formatRub(op.amountRub)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Paper>

          {/* ── Если продать сейчас ──────────────────────────────────────────────── */}
          <Paper withBorder p="md" radius="md" data-testid="if-sold-now-card">
            <Text fw={600} mb="xs">
              Если продать сейчас
            </Text>
            <SimpleGrid cols={{ base: 2, sm: 4 }}>
              <div>
                <Text size="xs" c="dimmed">
                  Рыночная стоимость
                </Text>
                <Text fw={600}>{formatRub(detail.ifSoldNow.marketValueRub)}</Text>
              </div>
              <div>
                <Text size="xs" c="dimmed">
                  Комиссия ({formatPercent(detail.ifSoldNow.commissionRate)})
                </Text>
                <Text fw={600} c="red">
                  −{formatRub(detail.ifSoldNow.commissionRub)}
                </Text>
              </div>
              <div>
                <Text size="xs" c="dimmed">
                  Выручка на руки
                </Text>
                <Text fw={700} size="lg" data-testid="if-sold-now-net-proceeds">
                  {formatRub(detail.ifSoldNow.netProceedsRub)}
                </Text>
              </div>
              <div>
                <Text size="xs" c="dimmed">
                  Итог (P&amp;L + купоны)
                </Text>
                {detail.ifSoldNow.pnlAvailable ? (
                  <Text fw={700} size="lg" c={(detail.ifSoldNow.totalReturnWithCouponsRub ?? 0) >= 0 ? 'green' : 'red'}>
                    {formatRub(detail.ifSoldNow.totalReturnWithCouponsRub)}
                  </Text>
                ) : (
                  <Text size="sm" c="dimmed">
                    Недоступно — журнал операций не покрывает остаток
                  </Text>
                )}
              </div>
            </SimpleGrid>
            <Text size="xs" c="dimmed" mt="sm">
              {detail.ifSoldNow.disclaimer}
            </Text>
          </Paper>

          <Text size="xs" c="dimmed">
            До {detail.calculatedToOffer ? 'оферты' : 'погашения'}: {formatDaysUntil(detail.horizonDate)}
          </Text>
        </>
      )}

      <Disclaimer text={detail?.disclaimer} />
    </Stack>
  );
}
