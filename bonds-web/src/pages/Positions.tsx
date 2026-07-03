import { useEffect, useMemo, useState } from 'react';
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
  ActionIcon,
} from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { usePositionsStore } from '../store/usePositionsStore';
import { useSettingsStore } from '../store/useSettingsStore';
import { Disclaimer } from '../components/Disclaimer';
import type { PositionRow } from '../api/types';
import { formatRub, formatDaysUntil, formatPercent, formatNumber, formatBp } from '../utils/format';

type SortKey = 'yield' | 'pnl';
type SortState = { key: SortKey; direction: 'asc' | 'desc' };

/** Эффективная доходность для отображения/сортировки: currentYield для floater/indexed, иначе ytmEffective. */
function effectiveYield(row: PositionRow): number | null {
  if (row.isFloater || row.isIndexed) return row.currentYield;
  return row.ytmEffective;
}

const COUPON_TYPE_LABEL: Record<PositionRow['couponType'], string> = {
  Fixed: 'Фиксированный',
  Floating: 'Плавающий',
  Indexed: 'Индексируемый',
};

const YIELD_TOOLTIP_TEXT =
  'YTM — эффективная доходность к погашению/оферте от текущей рыночной цены. ' +
  'Не зависит от вашей цены покупки. Доход от цены входа — колонка P&L.';

/**
 * Главный экран приложения после логина — таблица позиций с расчётными метриками (этап 09a,
 * цена входа/P&L — plan/14).
 * Графика/календарь/сигналы — 09b/09c.
 */
export function Positions() {
  const { positions, disclaimer, isLoading, error, load } = usePositionsStore();
  const { settings, load: loadSettings } = useSettingsStore();
  const navigate = useNavigate();
  const [sort, setSort] = useState<SortState>({ key: 'yield', direction: 'desc' });

  useEffect(() => {
    load();
    loadSettings();
  }, [load, loadSettings]);

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

  return (
    <Stack gap="md">
      <Title order={2}>Позиции</Title>

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить позиции" data-testid="positions-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && positions.length === 0 && settings && !settings.tInvestTokenConfigured && (
        <Alert color="violet" title="Нужно подключить брокерский счёт" data-testid="positions-empty-no-token">
          <Stack gap="sm">
            <Text size="sm">
              Чтобы увидеть позиции, укажите токен T-Invest (только на чтение) в настройках —
              он хранится в зашифрованном виде и используется для синхронизации портфеля.
            </Text>
            <Button
              variant="light"
              w="fit-content"
              onClick={() => navigate('/settings')}
              data-testid="goto-settings-button"
            >
              Перейти в настройки
            </Button>
          </Stack>
        </Alert>
      )}

      {!isLoading && !error && positions.length === 0 && settings?.tInvestTokenConfigured && (
        <Alert color="gray" title="Портфель пуст" data-testid="positions-empty">
          Токен подключён. Позиции появятся здесь после синхронизации с брокерским счётом —
          нажмите «Обновить данные» в шапке или подождите следующего автоматического цикла.
        </Alert>
      )}

      {!isLoading && !error && positions.length > 0 && (
        <>
          <Text size="xs" c="dimmed">
            Более низкая доходность не означает «хуже» без учёта срока до погашения и риска
            эмитента — сравнивайте бумаги с одинаковым горизонтом и качеством.
          </Text>

          <Table.ScrollContainer minWidth={1100}>
            <Table striped highlightOnHover data-testid="positions-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Бумага</Table.Th>
                  <Table.Th>Эмитент</Table.Th>
                  <Table.Th>Кол-во</Table.Th>
                  <Table.Th>Рыночная стоимость</Table.Th>
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
                    <Table.Tr key={row.positionId} data-testid={`position-row-${row.positionId}`}>
                      <Table.Td>{row.name ?? row.issuer ?? row.isin ?? '—'}</Table.Td>
                      <Table.Td>{row.issuer ?? '—'}</Table.Td>
                      <Table.Td>{row.quantity}</Table.Td>
                      <Table.Td>{formatRub(row.marketValueRub)}</Table.Td>
                      <Table.Td>{formatRub(row.averageCostRub)}</Table.Td>
                      <Table.Td>
                        <Tooltip label={totalReturnLabel} withArrow>
                          <Stack gap={0}>
                            <Text c={pnlColor} fw={500}>
                              {formatRub(row.unrealizedPnlRub)}
                            </Text>
                            <Text c={pnlColor} size="xs">
                              {formatPercent(row.unrealizedPnlPercent)}
                            </Text>
                          </Stack>
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>{formatRub(row.couponsReceivedRub)}</Table.Td>
                      <Table.Td>
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
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </>
      )}

      <Disclaimer text={disclaimer} />
    </Stack>
  );
}
