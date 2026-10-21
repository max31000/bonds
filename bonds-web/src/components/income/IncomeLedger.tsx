import { Button, Group, Loader, Table } from '@mantine/core';
import { EmptyState, ErrorState } from '@mvv42/kit';
import { IconExternalLink } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import type { IncomePosition } from '../../api/types';
import { ledgerKey, useIncomeStore } from '../../store/useIncomeStore';
import { cardLinkOf, ledgerKindLabel, signClass } from '../../utils/incomeDisplay';
import { formatDate, formatNumber, formatRub, formatRubPrecise } from '../../utils/format';

/**
 * Развёрнутая часть строки: хронология операций актива (лениво, кэш в сторе) и ссылка на карточку.
 * «Результат» — реализованный результат, признанный на этой операции (у покупок и купонов — 0).
 */
export function IncomeLedger({ position }: { position: IncomePosition }) {
  const state = useIncomeStore((s) => s.ledgers[ledgerKey(position.assetKind, position.assetId)]);
  const loadLedger = useIncomeStore((s) => s.loadLedger);
  const link = cardLinkOf(position);
  const testId = `income-ledger-${position.assetKind}-${position.assetId}`;

  return (
    <div className="bonds-ledger" data-testid={testId}>
      <Group justify="space-between" mb="xs" wrap="wrap">
        <div className="bonds-drilldown-title">Все операции по бумаге</div>
        {link && (
          <Button component={Link} to={link} variant="subtle" size="xs" rightSection={<IconExternalLink size={14} />} data-testid={`${testId}-open-card`}>
            Открыть карточку
          </Button>
        )}
      </Group>

      {(!state || state.status === 'loading') && (
        <div className="bonds-center">
          <Loader size="sm" />
        </div>
      )}

      {state?.status === 'error' && (
        <ErrorState
          title="Не удалось загрузить журнал операций"
          error={state.error}
          onRetry={() => void loadLedger(position.assetKind, position.assetId)}
          data-testid={`${testId}-error`}
        />
      )}

      {state?.status === 'ok' && state.data && state.data.entries.length === 0 && (
        <EmptyState compact title="Операций нет" description="Операций пока нет — они появятся после синхронизации с брокером." />
      )}

      {state?.status === 'ok' && state.data && state.data.entries.length > 0 && (
        <Table.ScrollContainer minWidth={640}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Дата</Table.Th>
                <Table.Th>Операция</Table.Th>
                <Table.Th ta="right">Кол-во</Table.Th>
                <Table.Th ta="right">Сумма</Table.Th>
                <Table.Th ta="right">в т.ч. НКД</Table.Th>
                <Table.Th ta="right">Остаток · ср. цена</Table.Th>
                <Table.Th ta="right">Результат</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {state.data.entries.map((e, idx) => (
                <Table.Tr key={`${e.date}-${e.kind}-${idx}`}>
                  <Table.Td>{formatDate(e.date)}</Table.Td>
                  <Table.Td>{ledgerKindLabel(e.kind, position.assetKind)}</Table.Td>
                  <Table.Td ta="right" className="mvv-num">
                    {e.quantity === null ? '—' : formatNumber(e.quantity, Number.isInteger(e.quantity) ? 0 : 2)}
                  </Table.Td>
                  <Table.Td ta="right" className={`mvv-num ${signClass(e.amountRub)}`}>
                    {formatRubPrecise(e.amountRub)}
                  </Table.Td>
                  <Table.Td ta="right" className="mvv-num">
                    {e.accruedRub === null ? '—' : formatRubPrecise(e.accruedRub)}
                  </Table.Td>
                  <Table.Td ta="right" className="mvv-num">
                    {formatNumber(e.quantityAfter, Number.isInteger(e.quantityAfter) ? 0 : 2)} · {formatRub(e.avgCostAfterRub)}
                  </Table.Td>
                  <Table.Td ta="right" className={`mvv-num ${signClass(e.realizedPnlRub)}`}>
                    {Math.abs(e.realizedPnlRub) < 0.005 ? '—' : formatRubPrecise(e.realizedPnlRub)}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </div>
  );
}
