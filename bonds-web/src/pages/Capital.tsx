import { useEffect } from 'react';
import { ErrorState, Page, Section } from '@mvv42/kit';
import { Skeleton } from '@mantine/core';
import { Disclaimer } from '../components/Disclaimer';
import { PlanActions } from '../components/capital/PlanActions';
import { PortfolioSection } from '../components/capital/PortfolioSection';
import { useBasketsStore } from '../store/useBasketsStore';
import { useCredentialsStore } from '../store/useCredentialsStore';

export const CAPITAL_DISCLAIMER = 'Оценка, не рекомендация: цели и отклонения — ваша собственная модель структуры капитала.';

/**
 * Страница «Капитал» (задача 62): целевая структура капитала по двум самостоятельным портфелям и отклонения от неё.
 * Рублёвый (T-Invest) виден всем, долларовый (Freedom) — только при `capabilities.freedom`. Только модель целей и расчёт
 * отклонений; планы сделок (задача 63, режим наблюдения) строятся кнопками блока «Планы сделок». Расчёт — на бэкенде, здесь показ и форма целей (ввод в %, в API — доли).
 */
export function Capital() {
  const { data, isLoading, error, load, save, reset } = useBasketsStore();
  const freedom = useCredentialsStore((s) => s.capabilities?.freedom === true);

  useEffect(() => {
    void load();
  }, [load]);

  const portfolios = (data?.portfolios ?? []).filter((p) => p.portfolio === 'rub' || freedom);

  return (
    <Page
      title="Капитал"
      description="Целевая структура капитала: корзины, их цели и отклонения от целей"
      width="wide"
      data-testid="capital-page"
    >
      {isLoading && !data && (
        <Section>
          <Skeleton height={120} />
        </Section>
      )}
      {error && !isLoading && (
        <Section>
          <ErrorState title="Не удалось загрузить корзины" error={error} onRetry={() => void load()} data-testid="capital-error" />
        </Section>
      )}
      {portfolios.some((p) => p.hasAccount) && <PlanActions portfolios={portfolios} freedom={freedom} />}
      {portfolios.map((p) => (
        <PortfolioSection
          key={p.portfolio}
          portfolio={p}
          onSave={save}
          onReset={() => reset(p.portfolio)}
        />
      ))}
      <Disclaimer text={data?.disclaimer || CAPITAL_DISCLAIMER} />
    </Page>
  );
}
