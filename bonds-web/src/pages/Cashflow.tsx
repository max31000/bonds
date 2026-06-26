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

/**
 * Экран денежного потока: календарь поступлений по месяцам (брутто/налог/нетто) +
 * даты освобождения тела долга (этап 09b §B.2).
 */
export function Cashflow() {
  const { byMonth, byPosition, principalReleases, disclaimer, isLoading, error, load } =
    useCashflowStore();
  const [byPositionOpen, setByPositionOpen] = useState(false);
  const [selectedMonth, setSelectedMonth] = useState<string | null>(null);

  useEffect(() => {
    load();
  }, [load]);

  const hasEstimatedAny = byMonth.some((m) => m.hasEstimatedFlows);

  const chartData = byMonth.map((m) => ({
    month: formatMonthLabel(m.month),
    'Налог': m.taxRub,
    'Нетто': m.netRub,
    raw: m,
  }));

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
          {byMonth.length === 0 ? (
            <Alert color="gray" title="Нет данных" data-testid="cashflow-empty">
              Календарь поступлений пуст — появится после синхронизации с брокерским счётом.
            </Alert>
          ) : (
            <Paper withBorder p="md" radius="md" data-testid="cashflow-chart">
              <Group justify="space-between" mb="xs">
                <Text fw={600}>Поступления по месяцам</Text>
                {hasEstimatedAny && (
                  <Badge size="sm" color="yellow" variant="light" data-testid="estimated-flows-badge" title="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке">
                    есть оценочные потоки
                  </Badge>
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
                                <Badge size="sm" color="yellow" variant="light" title="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке">оценочно</Badge>
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
                          <Badge size="sm" color="yellow" variant="light" title="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке">
                            оценочно
                          </Badge>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Paper>

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
                            <Badge size="sm" color="yellow" variant="light" title="Оценка: будущий купон флоатера неизвестен, посчитан по текущей ставке">
                              оценочно
                            </Badge>
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
