import { Badge, Group, Paper, Stack, Table, Text, Tooltip } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type { KeyboardEvent } from 'react';
import { IconAlertTriangle } from '@tabler/icons-react';
import { useNavigate } from 'react-router-dom';
import { Section } from '@mvv42/kit';
import type { CurrencyRow, CurrenciesTotals } from '../api/types';
import { formatDate, formatPercent, formatRub, formatRubPrecise } from '../utils/format';
import { signClass } from '../utils/incomeDisplay';
import { CURRENCY_DATA_INCOMPLETE, CURRENCY_HELP, currencyIncomeLink, CURRENCY_SOLD, CURRENCY_UNKNOWN_LOTS } from './currenciesCopy';
import { RateSparkline } from './RateSparkline';

/** Количество в ЕДИНИЦАХ валюты (юани, не лоты): целое без дробной части, иначе до 2 знаков. */
function formatQuantity(q: number): string {
  return new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 }).format(q);
}

function title(c: CurrencyRow): string {
  return c.name ?? c.isoCode;
}

function isSold(c: CurrencyRow): boolean {
  return c.quantity <= 0;
}

function Warnings({ c }: { c: CurrencyRow }) {
  const items: Array<[string, string]> = [];
  if (c.dataIncomplete) items.push(['data', CURRENCY_DATA_INCOMPLETE]);
  if (c.hasUnknownLots) items.push(['lots', CURRENCY_UNKNOWN_LOTS]);
  return (
    <>
      {items.map(([k, text]) => (
        <Tooltip key={k} label={text} multiline w={260} withArrow events={{ hover: true, focus: true, touch: true }}>
          <span role="img" tabIndex={0} aria-label={text} className="bonds-warn-icon" data-testid={`currency-warn-${k}-${c.currencyId}`}>
            <IconAlertTriangle size={14} />
          </span>
        </Tooltip>
      ))}
    </>
  );
}

/** Enter/Space на самой строке/карточке (не на вложенном контроле) — открыть журнал валюты. */
function activateOnKey(go: () => void) {
  return (e: KeyboardEvent) => {
    if (e.target !== e.currentTarget) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      go();
    }
  };
}

function Money({ value, signed = false }: { value: number | null; signed?: boolean }) {
  if (value === null) return <span className="bonds-note">—</span>;
  return (
    <span className={`mvv-num ${signClass(value)}`}>
      {signed && value > 0.005 ? '+' : ''}
      {formatRub(value)}
    </span>
  );
}

/** Изменение курса за 1/3/12 мес. — доли; null (истории не хватает) → «—». */
function RateChange({ c }: { c: CurrencyRow }) {
  const items: Array<[string, number | null]> = [
    ['1м', c.rateChange.m1],
    ['3м', c.rateChange.m3],
    ['12м', c.rateChange.m12],
  ];
  return (
    <Group gap={8} wrap="nowrap" data-testid={`currency-rate-change-${c.currencyId}`}>
      {items.map(([label, v]) => (
        <Text key={label} size="xs" c="dimmed" span>
          {label}{' '}
          <Text span fw={600} size="xs" className={signClass(v)}>
            {formatPercent(v)}
          </Text>
        </Text>
      ))}
    </Group>
  );
}

function Rate({ c }: { c: CurrencyRow }) {
  return (
    <>
      <div className="mvv-num">{formatRubPrecise(c.currentPriceRub)}</div>
      {c.priceAsOf && <div className="bonds-note">на {formatDate(c.priceAsOf)}</div>}
    </>
  );
}

function CurrencyCard({ c, onClick }: { c: CurrencyRow; onClick: () => void }) {
  return (
    <Paper
      withBorder
      p="sm"
      radius="md"
      data-testid={`currency-card-${c.currencyId}`}
      role="link"
      tabIndex={0}
      onClick={onClick}
      onKeyDown={activateOnKey(onClick)}
      style={{ cursor: 'pointer' }}
    >
      <Stack gap={4}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Group gap={6} wrap="nowrap">
            <Text fw={600} size="sm" lineClamp={2}>
              {title(c)}
            </Text>
            <Warnings c={c} />
          </Group>
          <Group gap={4} wrap="nowrap">
            {isSold(c) && (
              <Badge size="sm" color="gray" variant="light">
                {CURRENCY_SOLD}
              </Badge>
            )}
            <Badge size="sm" color="gray" variant="outline">
              {c.isoCode}
            </Badge>
          </Group>
        </Group>
        <Text fw={700}>{formatRub(c.marketValueRub)}</Text>
        <Text size="xs" c="dimmed">
          {formatQuantity(c.quantity)} {c.isoCode} × {formatRubPrecise(c.currentPriceRub)} · ср. курс {formatRubPrecise(c.avgCostRub)}
        </Text>
        <Group gap="md">
          <Text size="xs" className={signClass(c.unrealizedPnlRub)}>
            Курсовая разница {formatRub(c.unrealizedPnlRub)} ({formatPercent(c.unrealizedPnlFraction)})
          </Text>
        </Group>
        <Group gap="md">
          <Text size="xs" c="dimmed">
            Реализовано: <Text span fw={600}>{formatRub(c.realizedPnlRub)}</Text>
          </Text>
          <Text size="xs" c="dimmed">
            Итого: <Text span fw={600}>{formatRub(c.totalResultRub)}</Text>
          </Text>
        </Group>
        <Group gap="md" justify="space-between" wrap="nowrap">
          <RateChange c={c} />
          <RateSparkline points={c.rateHistory} label={`Курс ${c.isoCode} за год`} />
        </Group>
      </Stack>
    </Paper>
  );
}

/** Таблица валюты (вкладка «Валюта» на «Позициях»): десктоп — таблица с итогами, мобильная версия — карточки. */
export function CurrenciesTable({ currencies, totals }: { currencies: CurrencyRow[]; totals: CurrenciesTotals | null }) {
  const navigate = useNavigate();
  const isMobile = useMediaQuery('(max-width: 48em)');
  const totalPnlClass = signClass(totals?.unrealizedPnlRub ?? null);

  if (isMobile) {
    return (
      <Stack gap="sm" data-testid="currencies-cards">
        <Paper withBorder p="sm" radius="md" data-testid="currencies-totals-mobile">
          <Text size="xs" c="dimmed">
            Итого по валюте
          </Text>
          <Text fw={700}>{formatRub(totals?.marketValueRub)}</Text>
          <Text size="xs" className={totalPnlClass}>
            Курсовая разница {formatRub(totals?.unrealizedPnlRub)} · реализовано {formatRub(totals?.realizedPnlRub)} · итого {formatRub(totals?.totalResultRub)}
          </Text>
          {totals?.dataIncomplete && (
            <Text size="xs" c="orange" data-testid="currencies-totals-incomplete">
              {CURRENCY_DATA_INCOMPLETE}
            </Text>
          )}
        </Paper>
        {currencies.map((c) => (
          <CurrencyCard key={c.currencyId} c={c} onClick={() => navigate(currencyIncomeLink(c))} />
        ))}
      </Stack>
    );
  }

  return (
    <Section
      padding="none"
      title="Валюта на счёте"
      description={`${currencies.length} ${currencies.length === 1 ? 'валюта' : currencies.length < 5 ? 'валюты' : 'валют'} · строка открывает журнал в «Доходах»`}
    >
      <Table.ScrollContainer minWidth={1200}>
        <Table highlightOnHover data-testid="currencies-table">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Валюта</Table.Th>
              <Table.Th>Количество</Table.Th>
              <Table.Th>
                <Tooltip label={CURRENCY_HELP.avgCost} multiline w={260} withArrow>
                  <span>Ср. курс покупки</span>
                </Tooltip>
              </Table.Th>
              <Table.Th>Текущий курс</Table.Th>
              <Table.Th>Стоимость</Table.Th>
              <Table.Th>
                <Tooltip label={CURRENCY_HELP.unrealized} multiline w={260} withArrow>
                  <span>Курсовая разница</span>
                </Tooltip>
              </Table.Th>
              <Table.Th>
                <Tooltip label={CURRENCY_HELP.realized} multiline w={260} withArrow>
                  <span>Реализовано</span>
                </Tooltip>
              </Table.Th>
              <Table.Th>
                <Tooltip label={CURRENCY_HELP.total} multiline w={260} withArrow>
                  <span>Итого</span>
                </Tooltip>
              </Table.Th>
              <Table.Th>Изменение курса</Table.Th>
              <Table.Th>Курс за год</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {currencies.map((c) => (
              <Table.Tr
                key={c.currencyId}
                data-testid={`currency-row-${c.currencyId}`}
                role="link"
                tabIndex={0}
                onClick={() => navigate(currencyIncomeLink(c))}
                onKeyDown={activateOnKey(() => navigate(currencyIncomeLink(c)))}
                style={{ cursor: 'pointer' }}
              >
                <Table.Td>
                  <Group gap={6} wrap="nowrap">
                    <Text fw={500}>{title(c)}</Text>
                    <Warnings c={c} />
                    {isSold(c) && (
                      <Badge size="xs" color="gray" variant="light">
                        {CURRENCY_SOLD}
                      </Badge>
                    )}
                  </Group>
                  <Text size="xs" c="dimmed">
                    {c.isoCode}
                  </Text>
                </Table.Td>
                <Table.Td>{formatQuantity(c.quantity)}</Table.Td>
                <Table.Td>{isSold(c) ? '—' : formatRubPrecise(c.avgCostRub)}</Table.Td>
                <Table.Td>
                  <Rate c={c} />
                </Table.Td>
                <Table.Td>{formatRub(c.marketValueRub)}</Table.Td>
                <Table.Td>
                  <Money value={c.unrealizedPnlRub} signed />
                  <div className={`bonds-note ${signClass(c.unrealizedPnlRub)}`}>{formatPercent(c.unrealizedPnlFraction)}</div>
                </Table.Td>
                <Table.Td>
                  <Money value={c.realizedPnlRub} signed />
                </Table.Td>
                <Table.Td>
                  <Money value={c.totalResultRub} signed />
                </Table.Td>
                <Table.Td>
                  <RateChange c={c} />
                </Table.Td>
                <Table.Td>
                  <RateSparkline points={c.rateHistory} label={`Курс ${c.isoCode} за год`} />
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
          <Table.Tfoot>
            <Table.Tr data-testid="currencies-totals-row">
              <Table.Th>
                Итого
                {totals?.dataIncomplete && (
                  <Text size="xs" c="orange" fw={400} data-testid="currencies-totals-incomplete">
                    {CURRENCY_DATA_INCOMPLETE}
                  </Text>
                )}
              </Table.Th>
              <Table.Th />
              <Table.Th />
              <Table.Th />
              <Table.Th>{formatRub(totals?.marketValueRub)}</Table.Th>
              <Table.Th>
                <Text className={totalPnlClass} fw={700} size="sm">
                  {formatRub(totals?.unrealizedPnlRub)}
                </Text>
              </Table.Th>
              <Table.Th>{formatRub(totals?.realizedPnlRub)}</Table.Th>
              <Table.Th>{formatRub(totals?.totalResultRub)}</Table.Th>
              <Table.Th />
              <Table.Th />
            </Table.Tr>
          </Table.Tfoot>
        </Table>
      </Table.ScrollContainer>
    </Section>
  );
}
