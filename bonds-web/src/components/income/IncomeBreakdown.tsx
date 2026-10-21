import { useMemo } from 'react';
import { Tooltip } from '@mantine/core';
import { HelpHint, Section } from '@mvv42/kit';
import type { IncomeAccountLevel, IncomeTotals } from '../../api/types';
import { buildBreakdown, formatSignedRub, signClass } from '../../utils/incomeDisplay';
import { formatRub } from '../../utils/format';

/**
 * «Водопад» итога по компонентам: каждая строка — плавающий бар от накопленного значения до нового (зелёный — прибавка,
 * красный — вычет), последняя — итог. Построен на CSS, а не на графике: подписи сумм всегда читаются, строки доступны
 * скринридеру и хорошо складываются на узком экране.
 */
export function IncomeBreakdown({ totals, accountLevel }: { totals: IncomeTotals; accountLevel: IncomeAccountLevel }) {
  const items = useMemo(() => buildBreakdown(totals, accountLevel), [totals, accountLevel]);
  const lo = Math.min(0, ...items.flatMap((i) => [i.start, i.end]));
  const hi = Math.max(0, ...items.flatMap((i) => [i.start, i.end]));
  const span = hi - lo || 1;
  const pct = (v: number) => ((v - lo) / span) * 100;

  return (
    <Section
      title={
        <span className="bonds-inline-help">
          Из чего сложился результат
          <HelpHint label="Пояснение: Из чего сложился результат">
            Слева направо — как итог набирался: купоны и выплаты, затем поправки на НКД, налоги и комиссии, прибыль от продаж и бумажная прибыль. Последняя строка — итог за всё время.
          </HelpHint>
        </span>
      }
      data-testid="income-breakdown"
    >
      <ol className="bonds-breakdown">
        {items.map((it) => {
          const left = pct(Math.min(it.start, it.end));
          const width = Math.max(pct(Math.max(it.start, it.end)) - left, 0.6);
          const tone = it.isTotal ? 'total' : it.value >= 0 ? 'up' : 'down';
          return (
            <li key={it.key} className="bonds-breakdown-row" data-testid={`income-breakdown-${it.key}`} data-total={it.isTotal || undefined}>
              <Tooltip label={it.hint} multiline w={260} withArrow>
                <span className="bonds-breakdown-label" tabIndex={0}>
                  {it.label}
                </span>
              </Tooltip>
              <div className="bonds-breakdown-track" aria-hidden="true">
                <div className="bonds-breakdown-zero" style={{ left: `${pct(0)}%` }} />
                <div className="bonds-breakdown-bar" data-tone={tone} style={{ left: `${left}%`, width: `${width}%` }} />
              </div>
              <span className={`bonds-breakdown-value mvv-num ${signClass(it.value)}`}>
                {it.isTotal ? formatRub(it.value) : formatSignedRub(it.value, formatRub)}
              </span>
            </li>
          );
        })}
      </ol>
    </Section>
  );
}
