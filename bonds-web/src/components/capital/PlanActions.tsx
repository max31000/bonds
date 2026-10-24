import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Button, Checkbox, Group, Modal, NumberInput, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { Section } from '@mvv42/kit';
import { createExitBasketPlan, createInflowPlan, createWithdrawPlan } from '../../api/plans';
import type { CapitalPortfolio, CapitalPortfolioId, PlanResult } from '../../api/types';
import { usePlansStore } from '../../store/usePlansStore';
import { percentToFraction } from '../../utils/baskets';
import { formatPlanMoney } from '../../utils/plans';

type DialogKind = 'inflow' | 'withdraw' | 'exit';

const PORTFOLIO_LABEL: Record<CapitalPortfolioId, string> = { rub: 'Рублёвый (T-Invest)', usd: 'Долларовый (Freedom)' };

/** Результат создания плана: ссылка на «Планы», либо причины, по которым плана нет. */
function ResultView({ result, onClose }: { result: PlanResult; onClose: () => void }) {
  return (
    <Stack gap="xs" data-testid="plan-action-result">
      {result.plan ? (
        <Alert color="green" title="План создан" data-testid="plan-action-created">
          «{result.plan.trigger}» — {result.plan.lines.length} стр., ждёт вашего решения на странице «Планы». {result.plan.dryRun ? 'Режим наблюдения: сделки не отправляются.' : 'Исполнение включено: после подтверждения заявки отправит сервер.'}
          {result.plan.summary && result.plan.summary.requestedNet != null && (
            <Text size="sm" mt="xs">
              К выводу после налога и комиссии: {formatPlanMoney(result.plan.summary.netToWithdraw, result.plan.summary.currency)}
            </Text>
          )}
        </Alert>
      ) : (
        <Alert color="yellow" title="Плана нет" data-testid="plan-action-none">
          {result.notes.length > 0 ? result.notes.join(' ') : 'Правило не нашло, что предложить.'}
        </Alert>
      )}
      <Group gap="xs">
        {result.plan && (
          <Button component={Link} to="/plans" size="xs" onClick={onClose} data-testid="plan-action-open-plans">
            Открыть «Планы»
          </Button>
        )}
        <Button size="xs" variant="default" onClick={onClose}>
          Закрыть
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * Кнопки планов на странице «Капитал» (задача 63): «Распределить сумму…», «Вывести…», «Выйти из временной корзины». Создают планы
 * (`POST /api/plans/*`) в режиме наблюдения — заявки брокеру не уходят. Ввод процентов для вывода — в %, в API доля (`percentToFraction`, один раз).
 */
export function PlanActions({ portfolios, freedom }: { portfolios: CapitalPortfolio[]; freedom: boolean }) {
  const [dialog, setDialog] = useState<DialogKind | null>(null);
  const [portfolio, setPortfolio] = useState<CapitalPortfolioId>('rub');
  const [amount, setAmount] = useState<number | string>('');
  const [mode, setMode] = useState<'amount' | 'fraction'>('amount');
  const [included, setIncluded] = useState<string[]>([]);
  const [exitCode, setExitCode] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<PlanResult | null>(null);
  const loadPlans = usePlansStore((s) => s.load);

  const selected = portfolios.find((p) => p.portfolio === portfolio);
  const currency = selected?.currency ?? 'RUB';
  const exitable = useMemo(
    () => portfolios.flatMap((p) => p.baskets.filter((b) => !b.isSystem && b.targetWeight === null && b.members.length > 0)),
    [portfolios],
  );
  const optIn = useMemo(
    () => (selected?.baskets ?? []).filter((b) => !b.isSystem && (b.fillRule === 'hold' || (b.liquidityTier ?? 0) > 2)),
    [selected],
  );

  const close = () => {
    setDialog(null);
    setResult(null);
    setError(null);
  };

  const open = (kind: DialogKind) => {
    setDialog(kind);
    setResult(null);
    setError(null);
    setAmount('');
    setIncluded([]);
    setExitCode(exitable[0]?.code ?? null);
  };

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      let created: PlanResult;
      if (dialog === 'inflow') {
        created = await createInflowPlan({ amount: Number(amount), portfolio });
      } else if (dialog === 'withdraw') {
        created = await createWithdrawPlan({
          ...(mode === 'amount' ? { amount: Number(amount) } : { fraction: percentToFraction(Number(amount)) }),
          portfolio,
          ...(included.length > 0 ? { includeBaskets: included } : {}),
        });
      } else {
        created = await createExitBasketPlan(exitCode ?? '');
      }
      setResult(created);
      void loadPlans();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось построить план');
    } finally {
      setBusy(false);
    }
  };

  const amountValid = typeof amount === 'number' && amount > 0 && (mode === 'amount' || dialog !== 'withdraw' || amount <= 100);
  const canSubmit = dialog === 'exit' ? exitCode !== null : amountValid;

  return (
    <Section
      title="Планы сделок"
      description="Планы строятся и ждут решения; без включённого исполнения сделки не отправляются"
      data-testid="capital-plan-actions"
      actions={
        <Group gap="xs">
          <Button size="xs" variant="light" onClick={() => open('inflow')} data-testid="plan-action-inflow">
            Распределить сумму…
          </Button>
          <Button size="xs" variant="light" onClick={() => open('withdraw')} data-testid="plan-action-withdraw">
            Вывести…
          </Button>
          <Button size="xs" variant="light" onClick={() => open('exit')} data-testid="plan-action-exit">
            Выйти из временной корзины
          </Button>
        </Group>
      }
    >
      <Text size="sm" c="dimmed">
        Планы появляются и после синка автоматически. Откройте страницу «Планы», чтобы подтвердить или отклонить.
      </Text>
      <Modal
        opened={dialog !== null}
        onClose={close}
        title={dialog === 'inflow' ? 'Распределить сумму' : dialog === 'withdraw' ? 'Вывести средства' : 'Выйти из временной корзины'}
        data-testid="plan-action-dialog"
      >
        {result ? (
          <ResultView result={result} onClose={close} />
        ) : (
          <Stack gap="sm">
            {freedom && dialog !== 'exit' && (
              <Select
                label="Портфель"
                data={(['rub', 'usd'] as const).map((id) => ({ value: id, label: PORTFOLIO_LABEL[id] }))}
                value={portfolio}
                onChange={(v) => setPortfolio((v as CapitalPortfolioId) ?? 'rub')}
                allowDeselect={false}
                data-testid="plan-action-portfolio"
              />
            )}
            {dialog === 'inflow' && (
              <NumberInput
                label={`Сумма, ${currency}`}
                description="Распределится по корзинам пропорционально недобору до цели"
                value={amount}
                onChange={setAmount}
                min={0}
                decimalScale={2}
                hideControls
                data-testid="plan-action-amount"
              />
            )}
            {dialog === 'withdraw' && (
              <>
                <SegmentedControl
                  value={mode}
                  onChange={(v) => {
                    setMode(v as 'amount' | 'fraction');
                    setAmount('');
                  }}
                  data={[
                    { value: 'amount', label: 'Сумма' },
                    { value: 'fraction', label: '% портфеля' },
                  ]}
                  data-testid="plan-action-mode"
                />
                <NumberInput
                  label={mode === 'amount' ? `Сумма к выводу после налога и комиссии, ${currency}` : 'Доля портфеля, %'}
                  value={amount}
                  onChange={setAmount}
                  min={0}
                  max={mode === 'fraction' ? 100 : undefined}
                  decimalScale={2}
                  hideControls
                  data-testid="plan-action-amount"
                />
                {optIn.length > 0 && (
                  <Checkbox.Group label="Разрешить продавать также" value={included} onChange={setIncluded}>
                    <Stack gap={4} mt={4}>
                      {optIn.map((b) => (
                        <Checkbox key={b.code} value={b.code} label={b.title} data-testid={`plan-action-include-${b.code}`} />
                      ))}
                    </Stack>
                  </Checkbox.Group>
                )}
              </>
            )}
            {dialog === 'exit' &&
              (exitable.length === 0 ? (
                <Text size="sm" c="dimmed" data-testid="plan-action-exit-empty">
                  Нет временных корзин с бумагами (корзин без цели с членами).
                </Text>
              ) : (
                <Select
                  label="Корзина"
                  data={exitable.map((b) => ({ value: b.code, label: b.title }))}
                  value={exitCode}
                  onChange={setExitCode}
                  allowDeselect={false}
                  data-testid="plan-action-exit-basket"
                />
              ))}
            {error && (
              <Text size="sm" c="red" data-testid="plan-action-error">
                {error}
              </Text>
            )}
            <Group gap="xs">
              <Button size="xs" loading={busy} disabled={!canSubmit} onClick={() => void submit()} data-testid="plan-action-submit">
                Построить план
              </Button>
              <Button size="xs" variant="default" onClick={close}>
                Отмена
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Section>
  );
}
