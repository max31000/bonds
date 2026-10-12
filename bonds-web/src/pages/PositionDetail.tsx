import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Group, Loader, SegmentedControl, Table } from '@mantine/core';
import { EmptyState, ErrorState, HelpHint, Metric, MetricGrid, Page, Section } from '@mvv42/kit';
import { ChartFrame, ChartTooltip, chartColors, pickAxisTicks, useChartTheme } from '@mvv42/kit/charts';
import { IconArrowLeft } from '@tabler/icons-react';
import { CartesianGrid, Line, LineChart, ReferenceDot, ReferenceLine, Tooltip, XAxis, YAxis } from 'recharts';
import { fetchPositionDetail } from '../api/positions';
import { Disclaimer } from '../components/Disclaimer';

import type { PositionDetail as PositionDetailDto, PriceHistoryRange } from '../api/types';
import { formatRub, formatPercent, formatBp, formatNumber, formatDate, commissionSourceLabel, formatDaysUntil } from '../utils/format';

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

/** Метрика бумаги с «?»-пояснением — `Metric` кита. */
function MetricCard({ label, value, explanation, extra }: MetricCardProps) {
  return (
    <Metric
      data-testid={`metric-card-${label}`}
      label={
        <Group gap={4} wrap="nowrap" component="span">
          {label}
          <HelpHint label={`Пояснение: ${label}`} data-testid={`metric-explain-${label}`}>{explanation}</HelpHint>
        </Group>
      }
      value={value}
      hint={extra}
    />
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

  const t = useChartTheme();

  if (!id) {
    return (
      <Page>
        <ErrorState
          title="Некорректная ссылка"
          error="Не указан идентификатор позиции."
          data-testid="position-detail-invalid-id"
        />
      </Page>
    );
  }

  const backButton = (
    <Button
      variant="subtle"
      color="gray"
      size="xs"
      leftSection={<IconArrowLeft size={14} />}
      onClick={() => navigate('/positions')}
      data-testid="back-to-positions"
    >
      Позиции
    </Button>
  );

  const nextCoupon = detail?.couponSchedule.find((c) => !c.isPast);

  return (
    <Page
      width="wide"
      data-testid="position-detail-page"
      eyebrow={backButton}
      title={detail ? (detail.name ?? detail.issuer ?? detail.isin) : 'Позиция'}
      description={
        detail
          ? `${detail.isin} · ${detail.issuer ?? '—'}${detail.sector ? ` · ${detail.sector}` : ''}`
          : undefined
      }
    >
      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить карточку позиции" error={error} data-testid="position-detail-error" />
        </Section>
      )}

      {!isLoading && !error && detail && (
        <>
          {/* ── Шапка: стоимость позиции и признаки бумаги ─────────────────────── */}
          <Section data-testid="position-detail-header">
            <div className="bonds-position-head">
              <div>
                <div className="bonds-eyebrow">Стоимость позиции</div>
                <div className="bonds-figure mvv-num">{formatRub(detail.marketValueRub)}</div>
                <div className="bonds-note">
                  {detail.quantity} шт × {formatRub(detail.dirtyPrice)} · до{' '}
                  {detail.calculatedToOffer ? 'оферты' : 'погашения'} {formatDaysUntil(detail.horizonDate)}
                </div>
              </div>
              <Group gap={6} wrap="wrap" justify="flex-end">
                <Badge color="gray">{COUPON_TYPE_LABEL[detail.couponType]}</Badge>
                {detail.isFloater && <Badge color="blue">плавающая</Badge>}
                {detail.isIndexed && <Badge color="teal">индексируемая</Badge>}
                {detail.calculatedToOffer && <Badge color="orange">к оферте</Badge>}
                {detail.dataIncomplete && <Badge color="red">данные неполные</Badge>}
                {detail.isOutOfScopeCurrency && <Badge color="grape">валютная / вне скоупа</Badge>}
              </Group>
            </div>
          </Section>

          {/* ── Метрики ──────────────────────────────────────────────────────────── */}
          <MetricGrid cols={3} data-testid="position-detail-metrics">
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
              value={formatDate(nextCoupon?.couponDate)}
              explanation="Дата ближайшей будущей купонной выплаты по графику бумаги."
              extra={nextCoupon?.valueForPositionRub !== undefined ? formatRub(nextCoupon?.valueForPositionRub ?? null) : undefined}
            />
          </MetricGrid>

          {/* ── График цены ──────────────────────────────────────────────────────── */}
          <ChartFrame
            title="Цена"
            data-testid="price-history-chart"
            explanation="Дневная цена бумаги (грязная — с учётом НКД), пересчитанная в рубли по номиналу. Точки — ваши покупки/продажи; пунктир — средняя цена входа, если она известна."
            actions={
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
            empty={priceChartData.length === 0}
            emptyTitle={<span data-testid="price-history-empty">Нет истории цены</span>}
            emptyDescription="История цены недоступна за выбранный период."
          >
            <LineChart data={priceChartData} margin={t.margin}>
              <CartesianGrid {...t.grid} />
              <XAxis dataKey="label" {...t.xAxis} ticks={pickAxisTicks(priceChartData.map((d) => d.label), t.maxTicks)} />
              <YAxis {...t.yAxis} tickFormatter={(v: number) => formatRub(v)} width={84} domain={['auto', 'auto']} />
              <Tooltip
                {...t.tooltip}
                content={({ active, payload }) => {
                  if (!active || !payload?.length) return null;
                  const point = payload[0].payload as (typeof priceChartData)[number];
                  return (
                    <ChartTooltip
                      title={point.label}
                      rows={[{ label: 'Цена', value: formatRub(point.priceRub), color: chartColors.accent }]}
                    />
                  );
                }}
              />
              <Line {...t.line} dataKey="priceRub" name="Цена" stroke={chartColors.accent} />
              {detail.averageCostRub !== null && (
                <ReferenceLine
                  y={detail.averageCostRub}
                  stroke={chartColors.neutral}
                  strokeDasharray="4 4"
                  ifOverflow="extendDomain"
                  label={{ value: 'Ср. цена входа', position: 'insideTopLeft', fontSize: 11, fill: chartColors.tick }}
                />
              )}
              {tradeMarkers.map((marker, idx) => (
                <ReferenceDot
                  key={`${marker.date}-${idx}`}
                  x={marker.label}
                  y={marker.priceRub}
                  r={5}
                  fill={marker.type === 'Buy' ? chartColors.positive : chartColors.negative}
                  stroke={chartColors.surface}
                  strokeWidth={2}
                />
              ))}
            </LineChart>
          </ChartFrame>

          {/* ── Если продать сейчас ──────────────────────────────────────────────── */}
          <Section
            title="Если продать сейчас"
            description="Оценка выручки и итога при продаже всей позиции по текущей цене"
            data-testid="if-sold-now-card"
          >
            <div className="bonds-figures">
              <div>
                <div className="bonds-eyebrow">Рыночная стоимость</div>
                <div className="bonds-figure-sm mvv-num">{formatRub(detail.ifSoldNow.marketValueRub)}</div>
                {detail.ifSoldNow.accruedTotalRub > 0 && (
                  <div className="bonds-note" data-testid="if-sold-now-accrued-caption">
                    в т.ч. НКД {formatRub(detail.ifSoldNow.accruedTotalRub)}
                  </div>
                )}
              </div>
              <div>
                <div className="bonds-eyebrow">Комиссия ({formatPercent(detail.ifSoldNow.commissionRate)})</div>
                <div className="bonds-figure-sm mvv-num bonds-negative">−{formatRub(detail.ifSoldNow.commissionRub)}</div>
              </div>
              <div>
                <div className="bonds-eyebrow">Выручка на руки</div>
                <div className="bonds-figure-sm mvv-num" data-testid="if-sold-now-net-proceeds">
                  {formatRub(detail.ifSoldNow.netProceedsRub)}
                </div>
              </div>
              <div>
                <div className="bonds-eyebrow">− НДФЛ (оценка, 13% с прибыли к средней цене входа)</div>
                {detail.ifSoldNow.taxEstimateRub !== null ? (
                  <div className="bonds-figure-sm mvv-num bonds-negative" data-testid="if-sold-now-tax-estimate">
                    −{formatRub(detail.ifSoldNow.taxEstimateRub)}
                  </div>
                ) : (
                  <div className="bonds-note" data-testid="if-sold-now-tax-unavailable">
                    налог не оценён: журнал операций неполон
                  </div>
                )}
              </div>
              <div>
                <div className="bonds-eyebrow">Итог после налога (P&amp;L + купоны − НДФЛ)</div>
                {detail.ifSoldNow.pnlAvailable ? (
                  detail.ifSoldNow.netAfterTaxRub !== null ? (
                    <div
                      className={`bonds-figure-sm mvv-num ${detail.ifSoldNow.netAfterTaxRub >= 0 ? 'bonds-positive' : 'bonds-negative'}`}
                      data-testid="if-sold-now-net-after-tax"
                    >
                      {formatRub(detail.ifSoldNow.netAfterTaxRub)}
                    </div>
                  ) : (
                    <div className="bonds-note" data-testid="if-sold-now-net-after-tax-unavailable">
                      налог не оценён: журнал операций неполон
                    </div>
                  )
                ) : (
                  <div className="bonds-note">Недоступно — журнал операций не покрывает остаток</div>
                )}
                {detail.ifSoldNow.pnlAvailable && (
                  <div className="bonds-note" data-testid="if-sold-now-pretax-caption">
                    до налога {formatRub(detail.ifSoldNow.totalReturnWithCouponsRub)}
                  </div>
                )}
              </div>
            </div>
            <div className="bonds-footnotes">
              {detail.ifSoldNow.accruedTotalRub > 0 && (
                <p className="bonds-note" data-testid="if-sold-now-formula">
                  выручка = чистая стоимость {formatRub(detail.ifSoldNow.cleanValueRub)} + НКД{' '}
                  {formatRub(detail.ifSoldNow.accruedTotalRub)} − комиссия {formatRub(detail.ifSoldNow.commissionRub)} ={' '}
                  {formatRub(detail.ifSoldNow.netProceedsRub)}
                </p>
              )}
              <p className="bonds-note" data-testid="if-sold-now-commission-source">
                Комиссия {formatPercent(detail.ifSoldNow.commissionRate)} —{' '}
                {commissionSourceLabel(detail.ifSoldNow.commissionRateSource)}
              </p>
              <p className="bonds-note">{detail.ifSoldNow.disclaimer}</p>
            </div>
          </Section>

          <div className="bonds-grid-2">
            {/* ── Календарь бумаги ───────────────────────────────────────────────── */}
            <Section title="Календарь бумаги" padding="none" data-testid="instrument-calendar">
              {detail.couponSchedule.length === 0 &&
              detail.amortizationSchedule.length === 0 &&
              detail.offerSchedule.length === 0 ? (
                <EmptyState
                  compact
                  title="Нет графика"
                  description="График купонов/амортизаций/оферт недоступен."
                  data-testid="instrument-calendar-empty"
                />
              ) : (
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Дата</Table.Th>
                      <Table.Th>Событие</Table.Th>
                      <Table.Th ta="right">На позицию</Table.Th>
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
                        <Table.Tr
                          key={row.key}
                          data-testid={row.isPast ? 'calendar-row-past' : 'calendar-row-future'}
                          className={row.isPast ? 'bonds-row-past' : undefined}
                        >
                          <Table.Td>{formatDate(row.date)}</Table.Td>
                          <Table.Td>{row.label}</Table.Td>
                          <Table.Td ta="right">{row.amount !== null ? formatRub(row.amount) : '—'}</Table.Td>
                          <Table.Td>
                            <Group gap={4} wrap="nowrap">
                              {row.isPast && (
                                <Badge size="xs" color="gray">
                                  прошло
                                </Badge>
                              )}
                              {!row.isKnown && (
                                <Badge size="xs" color="yellow">
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
            </Section>

            {/* ── Мои операции ───────────────────────────────────────────────────── */}
            <Section title="Мои операции" padding="none" data-testid="position-operations">
              {detail.operations.length === 0 ? (
                <EmptyState
                  compact
                  title="Операций нет"
                  description="Операций по бумаге в журнале нет."
                  data-testid="position-operations-empty"
                />
              ) : (
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Дата</Table.Th>
                      <Table.Th>Тип</Table.Th>
                      <Table.Th ta="right">Кол-во</Table.Th>
                      <Table.Th ta="right">Сумма</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {detail.operations.map((op) => (
                      <Table.Tr key={op.id}>
                        <Table.Td>{formatDate(op.date)}</Table.Td>
                        <Table.Td>{OPERATION_TYPE_LABEL[op.type] ?? op.type}</Table.Td>
                        <Table.Td ta="right">{op.quantity ?? '—'}</Table.Td>
                        <Table.Td ta="right" c={op.amountRub >= 0 ? 'green' : 'red'}>
                          {formatRub(op.amountRub)}
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Section>
          </div>
        </>
      )}

      <Disclaimer text={detail?.disclaimer} />
    </Page>
  );
}
