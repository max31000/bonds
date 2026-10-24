import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, Group, List, Stack, Text, TextInput } from '@mantine/core';
import { ErrorState, Section } from '@mvv42/kit';
import { fetchExecutionState, setExecutionMode, setExecutionPaused } from '../api/execution';
import type { ExecutionState } from '../api/types';

/** Слово, которое нужно ввести для включения исполнения (сервер проверяет то же самое, с учётом регистра). */
export const LIVE_CONFIRMATION_WORD = 'LIVE';

/**
 * «Исполнение сделок» (задача 65) на странице настроек. По умолчанию — режим наблюдения. Включение исполнения: нужен проверенный торговый токен
 * («Подключения» → «T-Invest (торговля)») и повторный ввод слова «LIVE»; реальная отправка заявок начнётся, только если исполнение разрешено и на сервере
 * (переменная окружения владельца) и нет паузы. Все условия и итог показываются как есть — расчёт только на бэкенде.
 */
export function ExecutionModeSection() {
  const [state, setState] = useState<ExecutionState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const next = await fetchExecutionState();
      setError(null);
      setState(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить режим исполнения');
    }
  }, []);

  useEffect(() => {
    let alive = true;
    fetchExecutionState()
      .then((next) => {
        if (alive) setState(next);
      })
      .catch((err: unknown) => {
        if (alive) setError(err instanceof Error ? err.message : 'Не удалось загрузить режим исполнения');
      });
    return () => {
      alive = false;
    };
  }, []);

  const run = async (action: () => Promise<ExecutionState>) => {
    setBusy(true);
    setActionError(null);
    try {
      setState(await action());
      setConfirmation('');
    } catch (err) {
      setActionError(err instanceof Error ? err.message : 'Не удалось изменить режим');
    } finally {
      setBusy(false);
    }
  };

  const canEnable = !!state?.tradeCredentialOk && confirmation === LIVE_CONFIRMATION_WORD && !busy;

  return (
    <Section
      title="Исполнение сделок"
      description="По умолчанию сервис только наблюдает: планы строятся, но заявки брокеру не отправляются"
      data-testid="execution-section"
    >
      {error && <ErrorState title="Не удалось загрузить режим исполнения" error={error} onRetry={() => void load()} />}
      {state && (
        <Stack gap="sm">
          <Group gap="xs">
            <Text fw={600}>Режим:</Text>
            <Badge color={state.mode === 'live' ? 'red' : 'gray'} data-testid="execution-mode">
              {state.mode === 'live' ? 'Исполнение (live)' : 'Наблюдение (observe)'}
            </Badge>
            <Badge variant="light" color={state.live ? 'green' : 'gray'} data-testid="execution-effective">
              {state.live ? 'Заявки отправляются' : 'Заявки не отправляются'}
            </Badge>
          </Group>
          {state.reasons.length > 0 && (
            <List size="sm" data-testid="execution-reasons">
              {state.reasons.map((r) => (
                <List.Item key={r}>{r}</List.Item>
              ))}
            </List>
          )}

          {state.mode === 'observe' ? (
            <Stack gap="xs">
              {!state.tradeCredentialOk && (
                <Text size="sm" c="dimmed" data-testid="execution-need-token">
                  Чтобы включить исполнение, добавьте и проверьте торговый токен T-Invest в «Подключениях» (токен на один основной счёт, с правом торговли).
                </Text>
              )}
              <Group align="end" gap="xs">
                <TextInput
                  label={`Введите «${LIVE_CONFIRMATION_WORD}», чтобы включить исполнение`}
                  value={confirmation}
                  onChange={(e) => setConfirmation(e.currentTarget.value)}
                  disabled={!state.tradeCredentialOk}
                  data-testid="execution-confirm-input"
                />
                <Button color="red" disabled={!canEnable} loading={busy} onClick={() => void run(() => setExecutionMode('live', confirmation))} data-testid="execution-enable">
                  Включить исполнение
                </Button>
              </Group>
            </Stack>
          ) : (
            <Group gap="xs">
              <Button variant="default" loading={busy} onClick={() => void run(() => setExecutionMode('observe'))} data-testid="execution-disable">
                Вернуть режим наблюдения
              </Button>
              {state.paused ? (
                <Button variant="light" loading={busy} onClick={() => void run(() => setExecutionPaused(false))} data-testid="execution-resume">
                  Снять паузу
                </Button>
              ) : (
                <Button color="red" variant="light" loading={busy} onClick={() => void run(() => setExecutionPaused(true))} data-testid="execution-pause">
                  Остановить (пауза)
                </Button>
              )}
            </Group>
          )}
          {actionError && (
            <Text size="sm" c="red" data-testid="execution-error">
              {actionError}
            </Text>
          )}
          <Text size="xs" c="dimmed">
            Исполнение — лимитные заявки по подтверждённым планам в пределах лимитов авто-подтверждения; это не инвестиционная рекомендация.
          </Text>
        </Stack>
      )}
    </Section>
  );
}
