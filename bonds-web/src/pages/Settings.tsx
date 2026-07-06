import { useEffect, useState } from 'react';
import {
  Title,
  Stack,
  Paper,
  Text,
  Alert,
  Loader,
  Center,
  Group,
  NumberInput,
  Button,
  PasswordInput,
  Badge,
  Divider,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useSettingsStore } from '../store/useSettingsStore';
import { commissionSourceLabel, formatPercent, formatRub } from '../utils/format';
import type { SettingsUpdateRequest } from '../api/types';

type ThresholdsForm = Omit<SettingsUpdateRequest, 'baseCurrency' | 'commissionRateOverride'>;

/**
 * Экран настроек: пороги триггеров сигналов + токен T-Invest (write-only) (план 09c §B.8).
 * Токен никогда не приходит обратно от API — после сохранения поле очищается, показываем
 * только tInvestTokenMasked/статус «задан».
 */
export function Settings() {
  const { settings, isLoading, error, load, save, saveToken } = useSettingsStore();
  const [tokenInput, setTokenInput] = useState('');
  const [savingToken, setSavingToken] = useState(false);
  const [savingThresholds, setSavingThresholds] = useState(false);
  const [savingCommission, setSavingCommission] = useState(false);

  useEffect(() => {
    load();
  }, [load]);

  const form = useForm<ThresholdsForm>({
    initialValues: {
      upcomingEventDaysThreshold: 0,
      uninvestedCashThresholdRub: 0,
      uninvestedCashLookbackDays: 0,
      yieldBelowAlternativeBpsThreshold: 0,
      maturityWindowDaysForAlternativeComparison: 0,
      defaultMaxConcentrationPercent: 0,
      durationDriftToleranceYears: 0,
    },
    validate: {
      upcomingEventDaysThreshold: (v) => (v < 0 || v > 365 ? 'От 0 до 365 дней' : null),
      uninvestedCashThresholdRub: (v) => (v < 0 ? 'Не может быть отрицательным' : null),
      uninvestedCashLookbackDays: (v) => (v < 0 || v > 365 ? 'От 0 до 365 дней' : null),
      yieldBelowAlternativeBpsThreshold: (v) => (v < 0 ? 'Не может быть отрицательным' : null),
      maturityWindowDaysForAlternativeComparison: (v) =>
        v < 0 || v > 3650 ? 'От 0 до 3650 дней' : null,
      defaultMaxConcentrationPercent: (v) => (v < 0 || v > 100 ? 'От 0 до 100%' : null),
      durationDriftToleranceYears: (v) => (v < 0 || v > 30 ? 'От 0 до 30 лет' : null),
    },
  });

  useEffect(() => {
    if (!settings) return;
    form.setValues({
      upcomingEventDaysThreshold: settings.upcomingEventDaysThreshold,
      uninvestedCashThresholdRub: settings.uninvestedCashThresholdRub,
      uninvestedCashLookbackDays: settings.uninvestedCashLookbackDays,
      yieldBelowAlternativeBpsThreshold: settings.yieldBelowAlternativeBpsThreshold,
      maturityWindowDaysForAlternativeComparison: settings.maturityWindowDaysForAlternativeComparison,
      defaultMaxConcentrationPercent: settings.defaultMaxConcentrationPercent,
      durationDriftToleranceYears: settings.durationDriftToleranceYears,
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settings]);

  // Plan/22 часть D: override вводится в ПРОЦЕНТАХ (UI-конвенция), конвертация в долю — на границе
  // API (handleSaveCommission ниже), бэкенд хранит и резолвит только доли (см. CLAUDE.md). Отдельная
  // mantine-форма (не useState) — синхронизация из settings через form.setValues, как у thresholds
  // выше, избегает прямого вызова setState в эффекте.
  const commissionForm = useForm<{ percent: number | '' }>({ initialValues: { percent: '' } });

  useEffect(() => {
    if (!settings) return;
    commissionForm.setValues({
      percent: settings.commissionRateOverride === null ? '' : settings.commissionRateOverride * 100,
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settings]);

  const handleSaveThresholds = form.onSubmit(async (values) => {
    if (!settings) return;
    setSavingThresholds(true);
    // Пороги — отдельная форма от override комиссии (см. handleSaveCommission) — сохраняем
    // текущее значение override как есть, не трогаем.
    const ok = await save({
      baseCurrency: settings.baseCurrency,
      commissionRateOverride: settings.commissionRateOverride,
      ...values,
    });
    setSavingThresholds(false);
    notifications.show({
      color: ok ? 'green' : 'red',
      title: ok ? 'Настройки сохранены' : 'Не удалось сохранить настройки',
      message: ok ? 'Пороги триггеров обновлены.' : 'Попробуйте ещё раз позже.',
    });
  });

  const handleSaveToken = async () => {
    if (!tokenInput.trim()) return;
    setSavingToken(true);
    const result = await saveToken(tokenInput.trim());
    setSavingToken(false);
    setTokenInput(''); // токен — write-only, очищаем форму независимо от результата

    // T-13/C: невалидный токен отвечает 422 с человекочитаемым сообщением ("токен не прошёл
    // проверку T-Invest") — показываем его вместо общей заглушки; валидный — подтверждаем счётом.
    notifications.show({
      color: result.ok ? 'green' : 'red',
      title: result.ok ? 'Токен сохранён' : 'Токен не прошёл проверку',
      message: result.ok
        ? result.validatedAccountIdMasked
          ? `Токен T-Invest обновлён, счёт подтверждён: ${result.validatedAccountIdMasked}.`
          : 'Токен T-Invest обновлён.'
        : result.error,
    });
  };

  const handleSaveCommission = async () => {
    if (!settings) return;
    setSavingCommission(true);
    // Проценты -> доля на границе API (конвенция бэкенда — см. doc-comment выше). Пустое поле — сброс override.
    const percent = commissionForm.values.percent;
    const commissionRateOverride = percent === '' ? null : percent / 100;
    const ok = await save({
      baseCurrency: settings.baseCurrency,
      upcomingEventDaysThreshold: settings.upcomingEventDaysThreshold,
      uninvestedCashThresholdRub: settings.uninvestedCashThresholdRub,
      uninvestedCashLookbackDays: settings.uninvestedCashLookbackDays,
      yieldBelowAlternativeBpsThreshold: settings.yieldBelowAlternativeBpsThreshold,
      maturityWindowDaysForAlternativeComparison: settings.maturityWindowDaysForAlternativeComparison,
      defaultMaxConcentrationPercent: settings.defaultMaxConcentrationPercent,
      durationDriftToleranceYears: settings.durationDriftToleranceYears,
      commissionRateOverride,
    });
    setSavingCommission(false);
    notifications.show({
      color: ok ? 'green' : 'red',
      title: ok ? 'Комиссия сохранена' : 'Не удалось сохранить комиссию',
      message: ok
        ? commissionRateOverride === null
          ? 'Override убран — снова применяется авто-оценка/дефолт.'
          : 'Override ставки комиссии обновлён.'
        : 'Проверьте значение (0-5%) и попробуйте ещё раз.',
    });
  };

  return (
    <Stack gap="md">
      <Title order={2}>Настройки</Title>

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" title="Не удалось загрузить настройки" data-testid="settings-error">
          {error}
        </Alert>
      )}

      {!isLoading && settings && (
        <>
          <Paper withBorder p="md" radius="md" data-testid="tinvest-token-section">
            <Text fw={600} mb="xs">
              Токен T-Invest
            </Text>
            <Group mb="sm">
              {settings.tInvestTokenConfigured ? (
                <Badge color="teal" variant="light">
                  задан{settings.tInvestTokenMasked ? `: ${settings.tInvestTokenMasked}` : ''}
                </Badge>
              ) : (
                <Badge color="gray" variant="light">
                  не задан
                </Badge>
              )}
            </Group>
            <Group align="flex-end" wrap="wrap">
              <PasswordInput
                placeholder="Новый read-only токен T-Invest"
                value={tokenInput}
                onChange={(e) => setTokenInput(e.currentTarget.value)}
                style={{ flex: 1, minWidth: 240 }}
                data-testid="tinvest-token-input"
              />
              <Button
                onClick={handleSaveToken}
                loading={savingToken}
                disabled={!tokenInput.trim()}
                data-testid="tinvest-token-save"
              >
                Сохранить токен
              </Button>
            </Group>
            <Text size="xs" c="dimmed" mt="xs">
              Токен никогда не отображается повторно после сохранения — только статус «задан».
            </Text>
          </Paper>

          <Divider />

          <Paper withBorder p="md" radius="md" data-testid="commission-section">
            <Text fw={600} mb="xs">
              Комиссия брокера
            </Text>
            <Stack gap={4} mb="sm">
              <Text size="sm" data-testid="commission-tariff">
                Тариф T-Invest: {settings.tInvestTariff ?? '—'}
              </Text>
              <Text size="sm" data-testid="commission-auto-estimate">
                {settings.commissionAutoEstimate
                  ? `Фактическая по сделкам: ≈${formatPercent(settings.commissionAutoEstimate.rate)} (${settings.commissionAutoEstimate.tradeCount} сделок за ${settings.commissionAutoEstimate.windowMonths} мес, оборот ${formatRub(settings.commissionAutoEstimate.turnoverRub)})`
                  : 'Фактическая по сделкам: недостаточно данных в журнале операций'}
              </Text>
              <Text size="sm" fw={600} data-testid="commission-effective">
                Применяется: {formatPercent(settings.commissionEffectiveRate)} (
                {commissionSourceLabel(settings.commissionEffectiveSource)})
              </Text>
            </Stack>
            <Group align="flex-end" wrap="wrap">
              <NumberInput
                label="Переопределить, %"
                placeholder="например, 0.05"
                min={0}
                max={5}
                step={0.01}
                decimalScale={4}
                style={{ width: 200 }}
                data-testid="commission-override-input"
                {...commissionForm.getInputProps('percent')}
              />
              <Button
                onClick={handleSaveCommission}
                loading={savingCommission}
                data-testid="commission-save"
              >
                Сохранить
              </Button>
            </Group>
            <Text size="xs" c="dimmed" mt="xs">
              Пусто — override не задан, применяется авто-оценка из журнала сделок либо дефолт 0.3%.
              Ввод в процентах, на бэкенде хранится и применяется как доля (0.05% = 0.0005).
            </Text>
          </Paper>

          <Divider />

          <Paper withBorder p="md" radius="md" data-testid="thresholds-section">
            <Text fw={600} mb="sm">
              Пороги триггеров сигналов
            </Text>
            <form onSubmit={handleSaveThresholds}>
              <Stack gap="sm">
                <NumberInput
                  label="Горизонт предстоящих событий, дней"
                  min={0}
                  max={365}
                  {...form.getInputProps('upcomingEventDaysThreshold')}
                />
                <NumberInput
                  label="Порог незаинвестированного кэша, ₽"
                  min={0}
                  {...form.getInputProps('uninvestedCashThresholdRub')}
                />
                <NumberInput
                  label="Окно проверки незаинвестированного кэша, дней"
                  min={0}
                  max={365}
                  {...form.getInputProps('uninvestedCashLookbackDays')}
                />
                <NumberInput
                  label="Порог «доходность ниже альтернативы», б.п."
                  min={0}
                  {...form.getInputProps('yieldBelowAlternativeBpsThreshold')}
                />
                <NumberInput
                  label="Окно сравнения с альтернативой до погашения, дней"
                  min={0}
                  max={3650}
                  {...form.getInputProps('maturityWindowDaysForAlternativeComparison')}
                />
                <NumberInput
                  label="Максимальная концентрация по эмитенту, %"
                  min={0}
                  max={100}
                  {...form.getInputProps('defaultMaxConcentrationPercent')}
                />
                <NumberInput
                  label="Допуск дрейфа дюрации, лет"
                  min={0}
                  max={30}
                  step={0.1}
                  decimalScale={2}
                  {...form.getInputProps('durationDriftToleranceYears')}
                />
                <Group justify="flex-end">
                  <Button type="submit" loading={savingThresholds} data-testid="thresholds-save">
                    Сохранить пороги
                  </Button>
                </Group>
              </Stack>
            </form>
          </Paper>
        </>
      )}
    </Stack>
  );
}
