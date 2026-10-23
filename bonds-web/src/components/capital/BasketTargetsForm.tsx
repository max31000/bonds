import { useMemo, useState } from 'react';
import { Alert, Button, Group, NumberInput, Stack, Text } from '@mantine/core';
import type { BasketBatchItem, CapitalBasket, CapitalPortfolio } from '../../api/types';
import type { BasketsSaveResult } from '../../store/useBasketsStore';
import { fractionToPercent, percentToFraction, targetsSumOk } from '../../utils/baskets';

interface RowState {
  /** Проценты из поля ввода; '' — цели нет (корзина вне целей). */
  target: number | string;
  band: number | string;
  hardBand: number | string;
}

const toNumber = (v: number | string): number | null => (v === '' || typeof v === 'string' ? null : v);

function initialRow(b: CapitalBasket): RowState {
  return {
    target: b.targetWeight === null ? '' : fractionToPercent(b.targetWeight),
    band: fractionToPercent(b.band ?? 0),
    hardBand: fractionToPercent(b.hardBand ?? 0),
  };
}

/**
 * Редактирование целей и допусков портфеля одной формой (цели нескольких корзин меняются атомарно: 15/85 → 20/80 по одной корзине
 * невозможно — сумма целей обязана оставаться 100%). Ввод — в ПРОЦЕНТАХ, в API уходят ДОЛИ: конверсия `percentToFraction` один раз при
 * отправке. Сумма целей показывается на лету; ошибка бэкенда (422) выводится как есть.
 */
export function BasketTargetsForm({
  portfolio,
  onSave,
  onCancel,
}: {
  portfolio: CapitalPortfolio;
  onSave: (items: BasketBatchItem[]) => Promise<BasketsSaveResult>;
  onCancel: () => void;
}) {
  const editable = useMemo(() => portfolio.baskets.filter((b) => !b.isSystem && b.id !== null), [portfolio.baskets]);
  const [rows, setRows] = useState<Record<string, RowState>>(() =>
    Object.fromEntries(editable.map((b) => [b.code, initialRow(b)])),
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const patch = (code: string, change: Partial<RowState>) =>
    setRows((prev) => ({ ...prev, [code]: { ...prev[code], ...change } }));

  const sumPercent = editable.reduce((acc, b) => acc + (toNumber(rows[b.code].target) ?? 0), 0);
  const sumOk = targetsSumOk(editable.map((b) => {
    const t = toNumber(rows[b.code].target);
    return t === null ? null : percentToFraction(t);
  }));

  const submit = async () => {
    const items: BasketBatchItem[] = editable.map((b) => {
      const r = rows[b.code];
      const target = toNumber(r.target);
      return {
        id: b.id as number,
        title: b.title,
        targetWeight: target === null ? null : percentToFraction(target),
        band: percentToFraction(toNumber(r.band) ?? 0),
        hardBand: percentToFraction(toNumber(r.hardBand) ?? 0),
        liquidityTier: b.liquidityTier ?? 0,
        fillRule: b.fillRule ?? 'hold',
        autoBuy: b.autoBuy ?? false,
        autoSell: b.autoSell ?? false,
        minTrade: b.minTrade ?? 0,
      };
    });
    setSaving(true);
    setError(null);
    const result = await onSave(items);
    setSaving(false);
    if (!result.ok) setError(result.error);
  };

  return (
    <Stack gap="sm" p="md" data-testid={`targets-form-${portfolio.portfolio}`}>
      <Text size="sm" c="dimmed">
        Цели и допуски — в процентах от портфеля без корзин «вне целей». Сумма целей должна быть 100%; пустая цель — корзина вне целей.
      </Text>
      {editable.map((b) => (
        <Group key={b.code} align="flex-end" wrap="wrap" gap="sm">
          <Text w={170} fw={600}>
            {b.title}
          </Text>
          <NumberInput
            label="Цель, %"
            min={0}
            max={100}
            step={1}
            decimalScale={2}
            w={110}
            value={rows[b.code].target}
            onChange={(v) => patch(b.code, { target: v })}
            data-testid={`target-input-${b.code}`}
          />
          <NumberInput
            label="Допуск, %"
            min={0}
            max={100}
            step={0.5}
            decimalScale={2}
            w={110}
            value={rows[b.code].band}
            onChange={(v) => patch(b.code, { band: v })}
            data-testid={`band-input-${b.code}`}
          />
          <NumberInput
            label="Жёсткий допуск, %"
            min={0}
            max={100}
            step={0.5}
            decimalScale={2}
            w={140}
            value={rows[b.code].hardBand}
            onChange={(v) => patch(b.code, { hardBand: v })}
            data-testid={`hard-input-${b.code}`}
          />
        </Group>
      ))}
      <Text size="sm" c={sumOk ? 'dimmed' : 'red'} data-testid={`targets-sum-${portfolio.portfolio}`}>
        Сумма целей: {fractionToPercent(percentToFraction(sumPercent)).toFixed(2).replace('.', ',')}%{sumOk ? '' : ' — должна быть 100%'}
      </Text>
      {error && (
        <Alert color="red" variant="light" data-testid={`targets-error-${portfolio.portfolio}`}>
          {error}
        </Alert>
      )}
      <Group gap="sm">
        <Button onClick={() => void submit()} loading={saving} data-testid={`targets-save-${portfolio.portfolio}`}>
          Сохранить цели
        </Button>
        <Button variant="default" onClick={onCancel} disabled={saving}>
          Отмена
        </Button>
      </Group>
    </Stack>
  );
}
