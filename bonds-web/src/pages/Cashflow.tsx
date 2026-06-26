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
  Table,
  Collapse,
  UnstyledButton,
  Tooltip as MantineTooltip,
  SegmentedControl,
  SimpleGrid,
} from '@mantine/core';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { useCashflowStore } from '../store/useCashflowStore';
import { Disclaimer } from '../components/Disclaimer';
import { formatRub, formatMonthLabel, formatDate } from '../utils/format';

const FLOW_TYPE_LABEL: Record<string, string> = {
  Coupon: 'Купон',
  Amortization: 'Амортизация',
  Redemption: 'Погашение',
  Maturity: 'Погашение',
  Offer: 'Оферта',
  Call: 'Колл-опцион',
};

type Horizon = '6m' | '12m' | '24m' | 'all';

function toDateParam(horizon: Horizon): string | undefined {
  if (horizon === 'all') return undefined;
  const months = { '6m': 6, '12m': 12, '24m': 24 }[horizon];
  const d = new Date();
  d.setMonth(d.getMonth() + months);
  return d.toISOString().slice(0, 10);
}

/**
 * Экран денежного потока: календарь поступлений по месяцам (брутто/налог/нетто) +
 * даты освобождения тела долга (этап 09b §B.2).
 */
export function Cashflow() {
  const { byMonth, byPosition, principalReleases, nextPayments, disclaimer, isLoading, error, load } =
    useCashflowStore();
  const [byPositionOpen, setByPositionOpen] = useState(false);
  const [selectedMonth, setSelectedMonth] = useState<string | null>(null);
  const [horizon, setHorizon] = useState<Horizon>('12m');

  useEffect(() => {
    load(toDateParam(horizon));
  }, [load, horizon]);

  const hasEstimatedAny = byMonth.some((m) => m.hasEstimatedFlows);

  const chartData = byMonth.map((m) => ({
    month: formatMonthLabel(m.month),
    'Налог': m.taxRub,
    'Нетто': m.netRub,
    raw: m,
  }));

  const totalNetRub = byMonth.reduce((s, m) => s + m.netRub, 0);
  const totalCouponGross = byMonth.reduce((s, m) => s + m.couponGrossRub, 0);
  const totalPrincipalGross = byMonth.reduce((s, m) => s + m.principalGrossRub, 0);
  const totalTaxRub = byMonth.reduce((s, m) => s + m.taxRub, 0);

  return (
    <Stack gap="md">
      <Title order={2}>Денежный поток</Title>

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить календарь поступлений" data-testid="cashflow-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && (
        <>
          <Group justify="flex-end" mb="md">
            <SegmentedControl
              value={horizon}
              onChange={(v) => setHorizon(v as Horizon)}
              data={[
                { value: '6m', label: '6 мес' },
                { value: '12m', label: '12 мес' },
                { value: '24m', label: '24 мес' },
                { value: 'all', label: 'Весь срок' },
              ]}
              size="xs"
            />
          </Group>

          {byMonth.length > 0 && (
            <SimpleGrid cols={4} mb="md" data-testid="cashflow-kpi-cards">
              <Paper withBorder p="sm" radius="md">
                <Text size="xs" c="dimmed">Нетто за период</Text>
                <Text fw={700} size="lg">{formatRub(totalNetRub)}</Text>
              </Paper>
              <Paper withBorder p="sm" radius="md">
                <Text size="xs" c="dimmed">Купоны (брутто)</Text>
                <Text fw={700} size="lg">{formatRub(totalCouponGross)}</Text>
              </Paper>
              <Paper withBorder p="sm" radius="md">
                <Text size="xs" c="dimmed">Возврат тела</Text>
                <Text fw={700} size="lg">{formatRub(totalPrincipalGross)}</Text>
              </Paper>
              <Paper withBorder p="sm" radius="md">
                <Text size="xs" c="dimmed">Налог</Text>
                <Text fw={700} size="lg">{formatRub(totalTaxRub)}</Text>
              </Paper>
            </SimpleGrid>
          )}

          {byMonth.length === 0 ? (
            <Alert color="gray" title="Нет данных" data-testid="cashflow-empty">
              Календарь поступлений пуст — появится после синхронизации с брокерским счётом.
            </Alert>
          ) : (
            <Paper withBorder p="md" radius="md" data-testid="cashflow-chart">
              <Group justify="space-between" mb="xs">
                <Text fw={600}>Поступления по месяцам</Text>
                {hasEstimatedAny && (
                  <MantineTooltip label="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке" withArrow>
                    <Badge size="sm" color="yellow" variant="light" data-testid="estimated-flows-badge">
                      есть оценочные потоки
                    </Badge>
                  </MantineTooltip>
                )}
              </Group>
              <Text size="xs" c="dimmed" mb="xs">
                Нажмите на столбец месяца, чтобы увидеть, какие бумаги формируют поступление.
              </Text>
              <ResponsiveContainer width="100%" height={320}>
                <BarChart
                  data={chartData}
                  style={{ cursor: 'pointer' }}
                  onClick={(data) => {
                    const monthKey = (data?.activePayload?.[0]?.payload as { raw?: { month?: string } })?.raw?.month;
                    if (monthKey) setSelectedMonth((prev) => (prev === monthKey ? null : monthKey));
                  }}
                >
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis dataKey="month" />
                  <YAxis tickFormatter={(v: number) => formatRub(v)} width={100} />
                  <Tooltip
                    formatter={(value) => formatRub(Number(value))}
                    labelFormatter={(label, payload) => {
                      const item = payload?.[0]?.payload as { raw?: { hasEstimatedFlows?: boolean } };
                      return item?.raw?.hasEstimatedFlows ? `${label} (есть оценочные потоки)` : label;
                    }}
                  />
                  <Legend />
                  <Bar dataKey="Нетто" stackId="flow" fill="var(--mantine-color-teal-6)" />
                  <Bar dataKey="Налог" stackId="flow" fill="var(--mantine-color-red-5)" />
                </BarChart>
              </ResponsiveContainer>
              {selectedMonth && (() => {
                const monthData = byMonth.find((m) => m.month === selectedMonth);
                if (!monthData || monthData.positions.length === 0) return null;
                return (
                  <Paper withBorder p="sm" radius="sm" mt="sm" data-testid="cashflow-month-drill-down">
                    <Text fw={500} mb="xs">Поступления в {formatMonthLabel(selectedMonth)}</Text>
                    <Table>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>Бумага</Table.Th>
                          <Table.Th>Тип</Table.Th>
                          <Table.Th>Брутто</Table.Th>
                          <Table.Th>Налог</Table.Th>
                          <Table.Th>Нетто</Table.Th>
                          <Table.Th>Пометки</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {monthData.positions.map((pos, idx) => (
                          <Table.Tr key={`${pos.positionId}-${pos.flowType}-${idx}`}>
                            <Table.Td>{pos.name ?? pos.issuer ?? `#${pos.positionId}`}</Table.Td>
                            <Table.Td>{FLOW_TYPE_LABEL[pos.flowType] ?? pos.flowType}</Table.Td>
                            <Table.Td>{formatRub(pos.grossRub)}</Table.Td>
                            <Table.Td>{formatRub(pos.taxRub)}</Table.Td>
                            <Table.Td>{formatRub(pos.netRub)}</Table.Td>
                            <Table.Td>
                              {pos.isEstimated && (
                                <MantineTooltip label="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке" withArrow>
                                  <Badge size="sm" color="yellow" variant="light">оценочно</Badge>
                                </MantineTooltip>
                              )}
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Paper>
                );
              })()}
            </Paper>
          )}

          <Paper withBorder p="md" radius="md" data-testid="principal-releases">
            <Text fw={600} mb="xs">
              Даты освобождения тела долга
            </Text>
            {principalReleases.length === 0 ? (
              <Text size="sm" c="dimmed">
                Нет запланированных дат амортизации/погашения/оферты/колла в горизонте.
              </Text>
            ) : (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Дата</Table.Th>
                    <Table.Th>Тип</Table.Th>
                    <Table.Th>Сумма</Table.Th>
                    <Table.Th>Пометки</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {principalReleases.map((r, idx) => (
                    <Table.Tr key={`${r.positionId}-${r.date}-${idx}`}>
                      <Table.Td>{formatDate(r.date)}</Table.Td>
                      <Table.Td>{FLOW_TYPE_LABEL[r.flowType] ?? r.flowType}</Table.Td>
                      <Table.Td>{formatRub(r.amountRub)}</Table.Td>
                      <Table.Td>
                        {r.isEstimated && (
                          <MantineTooltip label="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке" withArrow>
                            <Badge size="sm" color="yellow" variant="light">оценочно</Badge>
                          </MantineTooltip>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Paper>

          {nextPayments.length > 0 && (
            <Paper withBorder p="md" radius="md" data-testid="next-payments">
              <Text fw={600} mb="xs">Ближайшие поступления</Text>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Дата</Table.Th>
                    <Table.Th>Бумага</Table.Th>
                    <Table.Th>Тип</Table.Th>
                    <Table.Th>Нетто</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {nextPayments.map((p, idx) => (
                    <Table.Tr key={idx}>
                      <Table.Td>{formatDate(p.date)}</Table.Td>
                      <Table.Td>{p.name ?? p.issuer ?? '—'}</Table.Td>
                      <Table.Td>{FLOW_TYPE_LABEL[p.flowType] ?? p.flowType}</Table.Td>
                      <Table.Td>
                        {formatRub(p.netRub)}
                        {p.isEstimated && (
                          <MantineTooltip label="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке" withArrow>
                            <Badge size="xs" color="yellow" variant="light" ml={4}>~</Badge>
                          </MantineTooltip>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Paper>
          )}

          {byPosition.length > 0 && (
            <Paper withBorder p="md" radius="md">
              <UnstyledButton
                onClick={() => setByPositionOpen((o) => !o)}
                data-testid="toggle-by-position"
              >
                <Text fw={600}>
                  Разбивка по позициям {byPositionOpen ? '▲' : '▼'}
                </Text>
              </UnstyledButton>
              <Collapse expanded={byPositionOpen}>
                <Table mt="sm" data-testid="cashflow-by-position-table">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Позиция</Table.Th>
                      <Table.Th>Брутто</Table.Th>
                      <Table.Th>Налог</Table.Th>
                      <Table.Th>Нетто</Table.Th>
                      <Table.Th>Пометки</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {byPosition.map((p) => (
                      <Table.Tr key={p.positionId}>
                        <Table.Td>{p.name ?? p.issuer ?? `#${p.positionId}`}</Table.Td>
                        <Table.Td>{formatRub(p.grossRub)}</Table.Td>
                        <Table.Td>{formatRub(p.taxRub)}</Table.Td>
                        <Table.Td>{formatRub(p.netRub)}</Table.Td>
                        <Table.Td>
                          {p.hasEstimatedFlows && (
                            <MantineTooltip label="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке" withArrow>
                              <Badge size="sm" color="yellow" variant="light">оценочно</Badge>
                            </MantineTooltip>
                          )}
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Collapse>
            </Paper>
          )}
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Stack>
  );
}
