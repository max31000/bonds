import { useEffect, useState } from 'react';
import { Badge, Button, Group, Loader, Paper, PasswordInput, SimpleGrid, Stack, Text, TextInput } from '@mantine/core';
import { ErrorState, Section } from '@mvv42/kit';
import { notifications } from '@mantine/notifications';
import dayjs from 'dayjs';
import { useCredentialsStore } from '../store/useCredentialsStore';
import type { CredentialCategory, CredentialRow } from '../api/types';

/** Пояснения к провайдерам (только копирайт; сам реестр и статусы приходят с бэкенда). */
const PROVIDER_HINTS: Record<string, string[]> = {
  tinvest_read: [
    'Только на чтение. Хранится зашифрованным и используется для синхронизации портфеля.',
    'Токен никогда не отображается повторно после сохранения — только статус «задан».',
  ],
  tinvest_trade: ['Экспериментально: торговый токен понадобится в фазе 3, сейчас нигде не используется.'],
  freedom: [
    'Ключи создаются в веб-терминале Freedom: профиль → API-ключи. Нужны публичный и секретный ключ; используются только для чтения.',
    'Секретный ключ никогда не отображается повторно после сохранения.',
  ],
  claude_code: [
    'Выполните `claude setup-token` на своём компьютере и вставьте токен. Используется подписка Claude, а не API-ключ.',
  ],
};

const CATEGORY_TITLES: Record<CredentialCategory, string> = { broker: 'Брокеры', ai: 'ИИ' };
const CATEGORY_ORDER: CredentialCategory[] = ['broker', 'ai'];

function StatusBadge({ row }: { row: CredentialRow }) {
  if (row.status === 'ok') return <Badge color="teal">Подключено</Badge>;
  if (row.status === 'invalid') {
    return <Badge color="red">Ошибка{row.statusMessage ? `: ${row.statusMessage}` : ''}</Badge>;
  }
  if (row.status === 'unverified') return <Badge color="yellow">Не проверено</Badge>;
  return <Badge color="gray">Не задано</Badge>;
}

function CredentialCard({ row }: { row: CredentialRow }) {
  const save = useCredentialsStore((s) => s.save);
  const remove = useCredentialsStore((s) => s.remove);
  const [editing, setEditing] = useState(false);
  const [values, setValues] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const configured = row.status !== 'missing';
  const canSubmit = row.fields.every((f) => (values[f.name] ?? '').trim() !== '');
  const testId = `credential-${row.provider}`;

  const closeForm = () => {
    setEditing(false);
    setValues({}); // секрет — write-only, не держим в состоянии дольше необходимого
    setFormError(null);
  };

  const handleSave = async () => {
    setSaving(true);
    setFormError(null);
    const fields = Object.fromEntries(row.fields.map((f) => [f.name, (values[f.name] ?? '').trim()]));
    const result = await save(row.provider, fields);
    setSaving(false);
    if (result.ok) {
      closeForm();
      notifications.show({ color: 'green', title: 'Подключение сохранено', message: `${row.title}: проверка пройдена.` });
    } else {
      // 422: форма остаётся открытой, значения не сбрасываем — пользователь поправит опечатку.
      setFormError(result.error);
    }
  };

  const handleDelete = async () => {
    setDeleting(true);
    const result = await remove(row.provider);
    setDeleting(false);
    setConfirmingDelete(false);
    notifications.show({
      color: result.ok ? 'green' : 'red',
      title: result.ok ? 'Подключение удалено' : 'Не удалось удалить',
      message: result.ok ? `${row.title}: секрет удалён.` : result.error,
    });
  };

  return (
    <Paper withBorder p="md" radius="md" data-testid={testId}>
      <Stack gap="xs">
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="xs">
          <Group gap="xs">
            <Text fw={600}>{row.title}</Text>
            {row.experimental && (
              <Badge variant="light" color="orange">
                эксперимент
              </Badge>
            )}
          </Group>
          <StatusBadge row={row} />
        </Group>

        <Text size="sm" c="dimmed">
          Включает: {row.unlocks}
        </Text>

        {configured && row.last4 && (
          <Text size="sm" data-testid={`${testId}-mask`}>
            ••••{row.last4}
            {row.verifiedAt && (
              <Text span size="xs" c="dimmed">
                {' '}
                · проверено {dayjs(row.verifiedAt).format('DD.MM.YYYY HH:mm')}
              </Text>
            )}
          </Text>
        )}

        {(PROVIDER_HINTS[row.provider] ?? []).map((hint) => (
          <p key={hint} className="bonds-note">
            {hint}
          </p>
        ))}

        {editing && (
          <Stack gap="xs" data-testid={`${testId}-form`}>
            {row.fields.map((f) => {
              const Input = f.isSecret ? PasswordInput : TextInput;
              return (
                <Input
                  key={f.name}
                  label={f.title}
                  value={values[f.name] ?? ''}
                  onChange={(e) => {
                    const next = e.currentTarget.value; // читаем до апдейтера: currentTarget к тому моменту уже null
                    setValues((v) => ({ ...v, [f.name]: next }));
                  }}
                  autoComplete="off"
                  data-testid={`${testId}-input-${f.name}`}
                />
              );
            })}
            {formError && (
              <Text size="sm" c="red" role="alert" data-testid={`${testId}-error`}>
                {formError}
              </Text>
            )}
            <Group gap="xs">
              <Button onClick={handleSave} loading={saving} disabled={!canSubmit} data-testid={`${testId}-save`}>
                Сохранить
              </Button>
              <Button variant="default" onClick={closeForm} disabled={saving}>
                Отмена
              </Button>
            </Group>
          </Stack>
        )}

        {!editing && (
          <Group gap="xs">
            <Button size="xs" variant="light" onClick={() => setEditing(true)} data-testid={`${testId}-edit`}>
              {configured ? 'Заменить' : 'Задать'}
            </Button>
            {configured && !confirmingDelete && (
              <Button
                size="xs"
                variant="subtle"
                color="red"
                onClick={() => setConfirmingDelete(true)}
                data-testid={`${testId}-delete`}
              >
                Удалить
              </Button>
            )}
            {configured && confirmingDelete && (
              <Group gap="xs" data-testid={`${testId}-delete-confirm`}>
                <Text size="sm">Удалить подключение?</Text>
                <Button size="xs" color="red" loading={deleting} onClick={handleDelete} data-testid={`${testId}-delete-yes`}>
                  Да, удалить
                </Button>
                <Button size="xs" variant="default" onClick={() => setConfirmingDelete(false)} disabled={deleting}>
                  Нет
                </Button>
              </Group>
            )}
          </Group>
        )}
      </Stack>
    </Paper>
  );
}

/** Секция «Подключения» (задача 58): карточка на каждого провайдера секретов, сгруппированно «Брокеры» / «ИИ». */
export function CredentialsSection() {
  const { credentials, isLoading, error, load } = useCredentialsStore();

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Section
      title="Подключения"
      description="Токены брокеров и ИИ-провайдеров. Каждое подключение включает свою часть функционала. Секреты хранятся зашифрованными и после сохранения не показываются."
      data-testid="credentials-section"
    >
      {isLoading && credentials.length === 0 && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}
      {error && credentials.length === 0 && (
        <ErrorState title="Не удалось загрузить подключения" error={error} data-testid="credentials-error" />
      )}
      <Stack gap="lg">
        {CATEGORY_ORDER.map((category) => {
          const rows = credentials.filter((c) => c.category === category);
          if (rows.length === 0) return null;
          return (
            <Stack key={category} gap="xs" data-testid={`credentials-group-${category}`}>
              <Text fw={600} size="sm" c="dimmed">
                {CATEGORY_TITLES[category]}
              </Text>
              <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
                {rows.map((row) => (
                  <CredentialCard key={row.provider} row={row} />
                ))}
              </SimpleGrid>
            </Stack>
          );
        })}
      </Stack>
    </Section>
  );
}
