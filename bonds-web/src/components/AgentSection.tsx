import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, Group, NumberInput, Paper, Select, Stack, Switch, Text } from '@mantine/core';
import { ErrorState, Section } from '@mvv42/kit';
import { fetchAgentState, saveAgentSettings } from '../api/agent';
import type { AgentRun, AgentState } from '../api/types';
import { formatDateTime } from '../utils/format';

const KIND_LABELS: Record<string, string> = {
  DailyDigest: 'Дайджест дня',
  EventReview: 'Разбор события',
  MonthlyReview: 'Обзор месяца',
  OnDemand: 'Вопрос (/ask)',
};

const MODEL_OPTIONS = ['sonnet', 'opus', 'haiku'];

function modelData(current: string) {
  return MODEL_OPTIONS.includes(current) ? MODEL_OPTIONS : [current, ...MODEL_OPTIONS];
}

function runStatus(run: AgentRun): { label: string; color: string } {
  if (!run.finished) return { label: 'идёт', color: 'blue' };
  if (run.error || (run.exitCode ?? 0) !== 0) return { label: 'ошибка', color: 'red' };
  return { label: 'готово', color: 'green' };
}

/**
 * Раздел «Агент» (задача 68) на странице настроек: включение (только при подключённом Claude Code), модели, лимит запусков в сутки и история запусков.
 * Ответ агента — недоверенный текст: выводится ТОЛЬКО как текст (React экранирует; ни HTML, ни Markdown не рендерятся).
 */
export function AgentSection() {
  const [state, setState] = useState<AgentState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [enabled, setEnabled] = useState(false);
  const [model, setModel] = useState('sonnet');
  const [monthlyModel, setMonthlyModel] = useState('opus');
  const [limit, setLimit] = useState<number>(5);

  const apply = useCallback((next: AgentState) => {
    setState(next);
    setEnabled(next.enabled);
    setModel(next.model);
    setMonthlyModel(next.monthlyModel);
    setLimit(next.dailyRunLimit);
  }, []);

  const load = useCallback(async () => {
    try {
      apply(await fetchAgentState());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить настройки агента');
    }
  }, [apply]);

  useEffect(() => {
    let alive = true;
    fetchAgentState()
      .then((next) => {
        if (alive) apply(next);
      })
      .catch((err: unknown) => {
        if (alive) setError(err instanceof Error ? err.message : 'Не удалось загрузить настройки агента');
      });
    return () => {
      alive = false;
    };
  }, [apply]);

  const save = async () => {
    setBusy(true);
    setSaveError(null);
    try {
      apply(await saveAgentSettings({ enabled, model, monthlyModel, dailyRunLimit: limit }));
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Не удалось сохранить');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Section
      title="Агент"
      description="ИИ-агент (Claude Code) читает портфель и присылает дайджест, разборы событий и ответы на /ask в боте. Он не отправляет заявки и не меняет настройки"
      data-testid="agent-section"
    >
      {error && <ErrorState title="Не удалось загрузить настройки агента" error={error} onRetry={() => void load()} />}
      {state && (
        <Stack gap="sm">
          {!state.credentialOk && (
            <Text size="sm" c="dimmed" data-testid="agent-no-credential">
              Подключите Claude Code в «Подключениях» (токен из <code>claude setup-token</code>) — после этого агента можно включить.
            </Text>
          )}
          {!state.serverEnabled && (
            <Text size="sm" c="orange" data-testid="agent-server-disabled">
              ИИ-агенты отключены на сервере — запуски не выполняются.
            </Text>
          )}
          <Switch
            label="Агент включён"
            checked={enabled}
            disabled={!state.credentialOk && !enabled}
            onChange={(e) => setEnabled(e.currentTarget.checked)}
            data-testid="agent-enabled"
          />
          <Group grow align="flex-end">
            <Select label="Модель (рутина)" data={modelData(model)} value={model} onChange={(v) => v && setModel(v)} allowDeselect={false} data-testid="agent-model" />
            <Select
              label="Модель (обзор месяца)"
              data={modelData(monthlyModel)}
              value={monthlyModel}
              onChange={(v) => v && setMonthlyModel(v)}
              allowDeselect={false}
              data-testid="agent-monthly-model"
            />
            <NumberInput
              label="Запусков в сутки"
              min={1}
              max={state.maxDailyRunLimit}
              value={limit}
              onChange={(v) => setLimit(typeof v === 'number' ? v : Number(v) || 1)}
              data-testid="agent-limit"
            />
          </Group>
          <Group gap="sm">
            <Button onClick={() => void save()} loading={busy} data-testid="agent-save">
              Сохранить
            </Button>
            <Text size="sm" c="dimmed" data-testid="agent-runs-today">
              Сегодня запусков: {state.runsToday} из {state.dailyRunLimit}
            </Text>
          </Group>
          {saveError && (
            <Text size="sm" c="red" data-testid="agent-save-error">
              {saveError}
            </Text>
          )}
          <Text size="xs" c="dimmed">
            Расписание: дайджест по будням в 19:00 МСК, обзор — 1-го числа, разбор новых критичных событий — не чаще 3 раз в сутки, вопросы — /ask в боте. План, предложенный
            агентом, всегда ждёт вашего решения.
          </Text>

          <Text fw={600} mt="sm">
            История запусков
          </Text>
          {state.runs.length === 0 && (
            <Text size="sm" c="dimmed" data-testid="agent-runs-empty">
              Запусков ещё не было.
            </Text>
          )}
          {state.runs.map((run) => {
            const status = runStatus(run);
            return (
              <Paper key={run.id} withBorder p="sm" data-testid={`agent-run-${run.id}`}>
                <Group gap="xs" mb={4}>
                  <Text fw={600} size="sm">
                    {KIND_LABELS[run.kind] ?? run.kind}
                  </Text>
                  <Badge size="sm" variant="light" color={status.color}>
                    {status.label}
                  </Badge>
                  <Text size="xs" c="dimmed">
                    {formatDateTime(run.started)}
                    {run.model ? ` · ${run.model}` : ''}
                    {run.costUsd != null ? ` · ≈ $${run.costUsd.toFixed(2)}` : ''}
                  </Text>
                </Group>
                {run.question && (
                  <Text size="sm" fs="italic" mb={4}>
                    {run.question}
                  </Text>
                )}
                {run.resultText && (
                  <Text size="sm" style={{ whiteSpace: 'pre-wrap' }} data-testid={`agent-run-text-${run.id}`}>
                    {run.resultText}
                  </Text>
                )}
                {run.error && (
                  <Text size="sm" c="red">
                    {run.error}
                  </Text>
                )}
              </Paper>
            );
          })}
          <Text size="xs" c="dimmed" data-testid="agent-disclaimer">
            {state.disclaimer}
          </Text>
        </Stack>
      )}
    </Section>
  );
}
