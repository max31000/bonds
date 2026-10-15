import { useState } from 'react';
import { Accordion, ActionIcon, Alert, Badge, Button, Collapse, Group, Select, SimpleGrid, Stack, Table, Text, TextInput } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import {
  deleteIssuerRating,
  fetchIssuerFinancials,
  fetchIssuerRatings,
  saveIssuerFinancials,
  saveIssuerRating,
} from '../api/issuers';
import { useWidgetData } from '../hooks/useWidgetData';
import type { IssuerFinancialsAmountKey, IssuerFinancialsRequest } from '../api/types';
import { formatDate, formatRub } from '../utils/format';

/** ИНН юрлица (10 цифр) или ИП/физлица (12 цифр) — та же проверка, что на бэкенде (`IssuerEndpoints.InnPattern`). */
const INN_PATTERN = /^(\d{10}|\d{12})$/;

const AGENCY_OTHER = 'Другое';
const AGENCY_OPTIONS = ['АКРА', 'Эксперт РА', 'НКР', 'НРА', AGENCY_OTHER];

/** Основные строки ручной отчётности (форма 1/2 РСБУ): код строки — подсказка для тех, кто сверяется с бланком. */
const MAIN_FIELDS: { key: IssuerFinancialsAmountKey; label: string }[] = [
  { key: 'revenue', label: 'Выручка (2110)' },
  { key: 'operatingProfit', label: 'Прибыль от продаж (2200)' },
  { key: 'interestPaid', label: 'Проценты к уплате (2330)' },
  { key: 'equity', label: 'Капитал и резервы (1300)' },
  { key: 'longDebt', label: 'Долгосрочные заёмные средства (1410)' },
  { key: 'shortDebt', label: 'Краткосрочные заёмные средства (1510)' },
];

/** Дополнительные строки: питают коэффициенты текущей ликвидности, Altman Z и динамику; без них эти компоненты «нет данных». */
const EXTRA_FIELDS: { key: IssuerFinancialsAmountKey; label: string }[] = [
  { key: 'netProfit', label: 'Чистая прибыль (2400)' },
  { key: 'totalAssets', label: 'Баланс — активы всего (1600)' },
  { key: 'currentAssets', label: 'Оборотные активы (1200)' },
  { key: 'currentLiabilities', label: 'Краткосрочные обязательства (1500)' },
  { key: 'cash', label: 'Денежные средства (1250)' },
  { key: 'retainedEarnings', label: 'Нераспределённая прибыль (1370)' },
];

const GIRBO_STATUS_LABEL: Record<string, string> = {
  ok: 'ГИР БО: отчётность загружена автоматически',
  not_found: 'ГИР БО: отчётность не найдена',
  hidden: 'ГИР БО: эмитент скрыл отчётность',
  cb: 'ГИР БО: кредитная организация (отчётность не публикуется)',
  error: 'ГИР БО: ошибка загрузки, повторим позже',
};

/** Разбор суммы из поля ввода: пробелы (`\s` покрывает и неразрывные) убираем, запятая = десятичный разделитель. Рубли как есть, без пересчёта. */
function parseAmount(text: string): number | null {
  const normalized = text.replace(/\s/g, '').replace(',', '.');
  if (normalized === '') return null;
  const value = Number(normalized);
  return Number.isFinite(value) ? value : Number.NaN;
}

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

// ─── Рейтинги ───────────────────────────────────────────────────────────────────────────

function RatingsForm({ inn, onChanged }: { inn: string; onChanged: () => void }) {
  const [version, setVersion] = useState(0);
  const { data, isLoading, error } = useWidgetData(() => fetchIssuerRatings(inn), [inn, version]);
  const [agencyChoice, setAgencyChoice] = useState<string>(AGENCY_OPTIONS[0]);
  const [customAgency, setCustomAgency] = useState('');
  const [rating, setRating] = useState('');
  const [ratingDate, setRatingDate] = useState('');
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const refresh = () => {
    setVersion((v) => v + 1);
    onChanged();
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const agency = agencyChoice === AGENCY_OTHER ? customAgency.trim() : agencyChoice;
    if (!agency) {
      setFormError('Укажите название агентства');
      return;
    }
    setSaving(true);
    setFormError(null);
    try {
      await saveIssuerRating(inn, { agency, rating: rating.trim(), ratingDate, note: note.trim() || null });
      setRating('');
      setNote('');
      refresh();
    } catch (err) {
      // 422 — текст причины от бэкенда («rating не распознан…», «ratingDate не может быть в будущем»).
      setFormError(err instanceof Error ? err.message : 'Не удалось сохранить рейтинг');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (agency: string) => {
    setFormError(null);
    try {
      await deleteIssuerRating(inn, agency);
      refresh();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Не удалось удалить рейтинг');
    }
  };

  return (
    <Stack gap="sm" data-testid="issuer-ratings">
      {isLoading && <Text size="xs" c="dimmed">Загрузка…</Text>}
      {error && <Alert color="red" data-testid="issuer-ratings-error">{error}</Alert>}
      {data && data.ratings.length === 0 && (
        <Text size="xs" c="dimmed" data-testid="issuer-ratings-empty">
          Рейтингов вручную не введено.
        </Text>
      )}
      {data && data.ratings.length > 0 && (
        // Строки-flex вместо таблицы: на 375px пять колонок (агентство, рейтинг, дата, заметка, удалить) не помещаются.
        <Stack gap={4} data-testid="issuer-ratings-list">
          {data.ratings.map((r) => (
            <Group key={r.agency} justify="space-between" wrap="nowrap" align="flex-start" gap="xs" data-testid={`issuer-rating-${r.agency}`}>
              <Stack gap={0} style={{ minWidth: 0 }}>
                <Group gap={6} wrap="wrap">
                  <Text size="sm">{r.agency}</Text>
                  <Text size="sm" fw={600}>{r.rating}</Text>
                  <Text size="xs" c="dimmed">{formatDate(r.ratingDate)}</Text>
                  {r.isStale && (
                    <Badge size="xs" color="yellow" variant="light">
                      старше {data.freshMonths} мес. — не учитывается
                    </Badge>
                  )}
                </Group>
                {r.note && <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{r.note}</Text>}
              </Stack>
              <ActionIcon
                variant="subtle"
                color="red"
                aria-label={`Удалить рейтинг ${r.agency}`}
                onClick={() => void remove(r.agency)}
                data-testid={`issuer-rating-delete-${r.agency}`}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Group>
          ))}
        </Stack>
      )}

      <form onSubmit={(e) => void submit(e)} data-testid="issuer-rating-form">
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
          <Select
            label="Агентство"
            data={AGENCY_OPTIONS}
            value={agencyChoice}
            onChange={(v) => setAgencyChoice(v ?? AGENCY_OPTIONS[0])}
            allowDeselect={false}
            data-testid="issuer-rating-agency"
          />
          {agencyChoice === AGENCY_OTHER && (
            <TextInput
              label="Название агентства"
              value={customAgency}
              onChange={(e) => setCustomAgency(e.currentTarget.value)}
              data-testid="issuer-rating-agency-custom"
            />
          )}
          <TextInput
            label="Рейтинг"
            placeholder="например, A(RU) или ruBBB+"
            value={rating}
            onChange={(e) => setRating(e.currentTarget.value)}
            data-testid="issuer-rating-value"
          />
          <TextInput
            label="Дата присвоения"
            type="date"
            max={todayIso()}
            value={ratingDate}
            onChange={(e) => setRatingDate(e.currentTarget.value)}
            data-testid="issuer-rating-date"
          />
          <TextInput
            label="Заметка"
            value={note}
            onChange={(e) => setNote(e.currentTarget.value)}
            data-testid="issuer-rating-note"
          />
        </SimpleGrid>
        {formError && (
          <Alert color="red" mt="xs" data-testid="issuer-rating-error">
            {formError}
          </Alert>
        )}
        <Group justify="flex-end" mt="xs">
          <Button type="submit" size="xs" loading={saving} data-testid="issuer-rating-save">
            Сохранить рейтинг
          </Button>
        </Group>
      </form>
    </Stack>
  );
}

// ─── Отчётность ─────────────────────────────────────────────────────────────────────────

function FinancialsForm({ inn, onChanged }: { inn: string; onChanged: () => void }) {
  const [version, setVersion] = useState(0);
  const { data, isLoading, error } = useWidgetData(() => fetchIssuerFinancials(inn), [inn, version]);
  const [period, setPeriod] = useState(String(new Date().getFullYear() - 1));
  const [amounts, setAmounts] = useState<Partial<Record<IssuerFinancialsAmountKey, string>>>({});
  const [showExtra, setShowExtra] = useState(false);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const field = (key: IssuerFinancialsAmountKey, label: string) => (
    <TextInput
      key={key}
      label={label}
      inputMode="decimal"
      placeholder="₽"
      value={amounts[key] ?? ''}
      onChange={(e) => {
        const next = e.currentTarget.value;
        setAmounts((prev) => ({ ...prev, [key]: next }));
      }}
      data-testid={`issuer-financials-${key}`}
    />
  );

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const year = Number(period);
    if (!Number.isInteger(year)) {
      setFormError('Укажите отчётный год');
      return;
    }
    const body: IssuerFinancialsRequest = { period: year };
    for (const { key, label } of [...MAIN_FIELDS, ...EXTRA_FIELDS]) {
      const parsed = parseAmount(amounts[key] ?? '');
      if (parsed === null) continue;
      if (Number.isNaN(parsed)) {
        setFormError(`${label}: ожидается число (рубли)`);
        return;
      }
      body[key] = parsed;
    }
    if (Object.keys(body).length === 1) {
      setFormError('Заполните хотя бы одну строку отчётности');
      return;
    }

    setSaving(true);
    setFormError(null);
    try {
      await saveIssuerFinancials(inn, body);
      setAmounts({});
      setVersion((v) => v + 1);
      onChanged();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Не удалось сохранить отчётность');
    } finally {
      setSaving(false);
    }
  };

  return (
    <Stack gap="sm" data-testid="issuer-financials">
      {isLoading && <Text size="xs" c="dimmed">Загрузка…</Text>}
      {error && <Alert color="red" data-testid="issuer-financials-error">{error}</Alert>}
      {data?.girBoStatus && (
        <Text size="xs" c="dimmed" data-testid="issuer-financials-girbo-status">
          {GIRBO_STATUS_LABEL[data.girBoStatus] ?? `ГИР БО: ${data.girBoStatus}`}
        </Text>
      )}
      {data && data.years.length > 0 && (
        <Table withRowBorders fz="sm" data-testid="issuer-financials-list">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Год</Table.Th>
              <Table.Th>Источник</Table.Th>
              <Table.Th ta="right">Выручка</Table.Th>
              <Table.Th ta="right">Капитал</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data.years.map((y) => (
              <Table.Tr key={`${y.period}-${y.source}`} data-testid={`issuer-financials-year-${y.period}`}>
                <Table.Td>{y.period}</Table.Td>
                <Table.Td>
                  <Badge size="xs" variant="light" color={y.source === 'manual' ? 'blue' : 'gray'}>
                    {y.source === 'manual' ? 'вручную' : 'ГИР БО'}
                  </Badge>
                </Table.Td>
                <Table.Td ta="right">{formatRub(y.revenue)}</Table.Td>
                <Table.Td ta="right">{formatRub(y.equity)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <form onSubmit={(e) => void submit(e)} data-testid="issuer-financials-form">
        <Text size="xs" c="dimmed" mb="xs">
          Для банков и эмитентов, скрывших отчётность в ГИР БО. Суммы — в рублях (не в тысячах), как в консолидированной
          отчётности. Ручной ввод за год заменяет автоматические данные за этот год.
        </Text>
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
          <TextInput
            label="Отчётный год"
            inputMode="numeric"
            value={period}
            onChange={(e) => setPeriod(e.currentTarget.value)}
            data-testid="issuer-financials-period"
          />
          {MAIN_FIELDS.map((f) => field(f.key, f.label))}
        </SimpleGrid>
        <Button variant="subtle" size="compact-xs" mt="xs" onClick={() => setShowExtra((v) => !v)} data-testid="issuer-financials-extra-toggle">
          {showExtra ? 'Скрыть дополнительные строки' : 'Дополнительные строки (баланс, ликвидность)'}
        </Button>
        <Collapse expanded={showExtra}>
          <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs" mt="xs">
            {EXTRA_FIELDS.map((f) => field(f.key, f.label))}
          </SimpleGrid>
        </Collapse>
        {formError && (
          <Alert color="red" mt="xs" data-testid="issuer-financials-form-error">
            {formError}
          </Alert>
        )}
        <Group justify="flex-end" mt="xs">
          <Button type="submit" size="xs" loading={saving} data-testid="issuer-financials-save">
            Сохранить отчётность
          </Button>
        </Group>
      </form>
    </Stack>
  );
}

// ─── Панель и блок с вводом ИНН ─────────────────────────────────────────────────────────

/**
 * Свёрнутые формы ручного ввода данных эмитента (рейтинги агентств + годовая отчётность) для известного ИНН. Видны
 * всегда, раскрываются по клику; после записи вызывается <paramref name="onChanged"/> — родитель перезапрашивает
 * скоркарту (бэкенд уже сбросил её кэш). Ошибки 422 показываются инлайн текстом причины от бэкенда.
 */
export function IssuerDataPanel({ inn, onChanged }: { inn: string; onChanged: () => void }) {
  return (
    <Accordion multiple variant="separated" data-testid="issuer-data-panel">
      <Accordion.Item value="ratings">
        <Accordion.Control data-testid="issuer-ratings-toggle">Рейтинги агентств (вручную)</Accordion.Control>
        <Accordion.Panel>
          <RatingsForm inn={inn} onChanged={onChanged} />
        </Accordion.Panel>
      </Accordion.Item>
      <Accordion.Item value="financials">
        <Accordion.Control data-testid="issuer-financials-toggle">Отчётность (вручную)</Accordion.Control>
        <Accordion.Panel>
          <FinancialsForm inn={inn} onChanged={onChanged} />
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}

/**
 * Блок «Данные эмитента» (задача 47): ИНН (подставлен из карточки бумаги либо вводится вручную — на странице
 * Настроек и когда эмитент выпуска ещё не определён) + <see>IssuerDataPanel</see>.
 */
export function IssuerDataBlock({ defaultInn, onChanged }: { defaultInn?: string | null; onChanged?: () => void }) {
  const [innInput, setInnInput] = useState(defaultInn ?? '');
  const [activeInn, setActiveInn] = useState<string | null>(defaultInn && INN_PATTERN.test(defaultInn) ? defaultInn : null);
  const [innError, setInnError] = useState<string | null>(null);

  const apply = (event: React.FormEvent) => {
    event.preventDefault();
    const inn = innInput.trim();
    if (!INN_PATTERN.test(inn)) {
      setInnError('ИНН — 10 цифр (юрлицо) или 12 цифр (ИП)');
      setActiveInn(null);
      return;
    }
    setInnError(null);
    setActiveInn(inn);
  };

  return (
    <Stack gap="sm" data-testid="issuer-data-block">
      <form onSubmit={apply}>
        <Group align="flex-end" wrap="wrap">
          <TextInput
            label="ИНН эмитента"
            inputMode="numeric"
            value={innInput}
            onChange={(e) => setInnInput(e.currentTarget.value)}
            error={innError}
            style={{ flex: 1, minWidth: 200 }}
            data-testid="issuer-inn-input"
          />
          <Button type="submit" variant="light" data-testid="issuer-inn-apply">
            Показать
          </Button>
        </Group>
      </form>
      {!activeInn && defaultInn == null && (
        <Text size="xs" c="dimmed">
          ИНН эмитента этого выпуска пока не определён — введите его вручную.
        </Text>
      )}
      {activeInn && <IssuerDataPanel inn={activeInn} onChanged={onChanged ?? (() => undefined)} />}
    </Stack>
  );
}
