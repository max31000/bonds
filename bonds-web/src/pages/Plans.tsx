import { useEffect } from 'react';
import { EmptyState, ErrorState, Page, Section } from '@mvv42/kit';
import { Skeleton } from '@mantine/core';
import { Disclaimer } from '../components/Disclaimer';
import { ActiveOrdersBanner, DryRunBanner } from '../components/plans/DryRunBanner';
import { PlanCard } from '../components/plans/PlanCard';
import { usePlansStore } from '../store/usePlansStore';
import { isDecidable } from '../utils/plans';

export const PLANS_DISCLAIMER =
  'Режим наблюдения: сделки не отправляются. План — оценка по стакану на момент построения, не инвестиционная рекомендация.';

/**
 * Страница «Планы» (задача 63): планы сделок от правил (парковка денег, распределение пополнения, перелив ликвидности, ребаланс, вывод, доливка Freedom).
 * По умолчанию РЕЖИМ НАБЛЮДЕНИЯ: «Подтвердить» помечает план «не исполнен: dry-run». В живом режиме (задача 65, все ворота открыты на сервере) подтверждённый план
 * исполняет сервер: на карточке — статусы заявок по строкам и «Отменить заявки», в баннере — «Остановить» (пауза). Расчёт и исполнение — только на бэкенде.
 */
export function Plans() {
  const { data, isLoading, error, decidingId, load, approve, reject, cancelExecution, stop, releaseCashHold } = usePlansStore();

  useEffect(() => {
    void load();
  }, [load]);

  const items = data?.items ?? [];
  const waiting = items.filter((p) => isDecidable(p.status));
  const history = items.filter((p) => !isDecidable(p.status));

  return (
    <Page title="Планы" description="Какие сделки сервис рассчитал как кандидаты по правилам: оценка по ценам стакана, не рекомендация" width="wide" data-testid="plans-page">
      <DryRunBanner live={data?.dryRun === false} brokers={data?.brokers} onStop={stop} />
      <ActiveOrdersBanner count={data?.activeOrders ?? 0} />
      {isLoading && !data && (
        <Section>
          <Skeleton height={120} />
        </Section>
      )}
      {error && !isLoading && (
        <Section>
          <ErrorState title="Не удалось загрузить планы" error={error} onRetry={() => void load()} data-testid="plans-error" />
        </Section>
      )}
      {data && items.length === 0 && (
        <Section>
          <EmptyState
            title="Планов пока нет"
            description="Планы появляются после синка, когда правила находят, что предложить (свободные деньги, пополнение, отклонение корзин), либо по кнопкам на странице «Капитал»."
            data-testid="plans-empty"
          />
        </Section>
      )}
      {waiting.length > 0 && (
        <Section title="Ждут решения" padding="none" data-testid="plans-waiting">
          {waiting.map((plan) => (
            <PlanCard key={plan.id} plan={plan} busy={decidingId === plan.id} onApprove={approve} onReject={reject} onCancelExecution={cancelExecution} onReleaseCashHold={releaseCashHold} />
          ))}
        </Section>
      )}
      {history.length > 0 && (
        <Section title="История" padding="none" data-testid="plans-history">
          {history.map((plan) => (
            <PlanCard key={plan.id} plan={plan} busy={decidingId === plan.id} onApprove={approve} onReject={reject} onCancelExecution={cancelExecution} onReleaseCashHold={releaseCashHold} />
          ))}
        </Section>
      )}
      <Disclaimer text={data?.disclaimer || PLANS_DISCLAIMER} />
    </Page>
  );
}
