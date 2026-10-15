import { useEffect, useState } from 'react';
import { Badge, Button, Group, Loader, NumberInput, PasswordInput, SimpleGrid, Stack, Text } from '@mantine/core';
import { ErrorState, Page, Section } from '@mvv42/kit';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { IssuerDataBlock } from '../components/IssuerDataBlock';
import { useSettingsStore } from '../store/useSettingsStore';
import { commissionSourceLabel, formatPercent, formatRub } from '../utils/format';
import type { SettingsUpdateRequest } from '../api/types';

type ThresholdsForm = Omit<SettingsUpdateRequest, 'baseCurrency' | 'commissionRateOverride'>;

/**
 * Экран настроек: пороги правил событий + токен T-Invest (write-only) (план 09c §B.8).
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
      defaultMaxConcentrationPercent: 0,
    },
    validate: {
      upcomingEventDaysThreshold: (v) => (v < 0 || v > 365 ? 'От 0 до 365 дней' : null),
      uninvestedCashThresholdRub: (v) => (v < 0 ? 'Не может быть отрицательным' : null),
      defaultMaxConcentrationPercent: (v) => (v < 0 || v > 100 ? 'От 0 до 100%' : null),
    },
  });

  useEffect(() => {
    if (!settings) return;
    form.setValues({
      upcomingEventDaysThreshold: settings.upcomingEventDaysThreshold,
      uninvestedCashThresholdRub: settings.uninvestedCashThresholdRub,
      defaultMaxConcentrationPercent: settings.defaultMaxConcentrationPercent,
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settings]);

  // override вводится в ПРОЦЕНТАХ (UI-конвенция), конвертация в долю — на границе
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
      message: ok ? 'Пороги событий обновлены.' : 'Попробуйте ещё раз позже.',
    });
  });

  const handleSaveToken = async () => {
    if (!tokenInput.trim()) return;
    setSavingToken(true);
    const result = await saveToken(tokenInput.trim());
    setSavingToken(false);
    setTokenInput(''); // токен — write-only, очищаем форму независимо от результата

    // невалидный токен отвечает 422 с человекочитаемым сообщением ("токен не прошёл
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
      defaultMaxConcentrationPercent: settings.defaultMaxConcentrationPercent,
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
    <Page title="Настройки" description="Брокерский счёт, комиссия и пороги событий">
      {isLoading && (
        <div className="bonds-center">
          <Loader />
        </div>
      )}

      {!isLoading && error && (
        <Section>
          <ErrorState title="Не удалось загрузить настройки" error={error} data-testid="settings-error" />
        </Section>
      )}

      {!isLoading && settings && (
        <>
          <Section
            title="Токен T-Invest"
            description="Только на чтение. Хранится зашифрованным и используется для синхронизации портфеля."
            actions={
              settings.tInvestTokenConfigured ? (
                <Badge color="teal">
                  задан{settings.tInvestTokenMasked ? `: ${settings.tInvestTokenMasked}` : ''}
                </Badge>
              ) : (
                <Badge color="gray">не задан</Badge>
              )
            }
            data-testid="tinvest-token-section"
          >
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
            <p className="bonds-note bonds-mt">
              Токен никогда не отображается повторно после сохранения — только статус «задан».
            </p>
          </Section>

          <Section
            title="Комиссия брокера"
            description="Используется в оценках «если продать сейчас», замен и корзины"
            data-testid="commission-section"
          >
            <Stack gap={4} mb="md">
              <Text size="sm" c="dimmed" data-testid="commission-tariff">
                Тариф T-Invest: {settings.tInvestTariff ?? '—'}
              </Text>
              <Text size="sm" c="dimmed" data-testid="commission-auto-estimate">
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
            <p className="bonds-note bonds-mt">
              Пусто — override не задан, применяется авто-оценка из журнала сделок либо дефолт 0.3%.
              Ввод в процентах, на бэкенде хранится и применяется как доля (0.05% = 0.0005).
            </p>
          </Section>

          <Section
            title="Пороги событий"
            description="Когда в «Событиях» появляются оферты, погашения, свободные деньги и превышение лимита концентрации"
            data-testid="thresholds-section"
          >
            <form onSubmit={handleSaveThresholds}>
              <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md" verticalSpacing="sm">
                <NumberInput
                  label="Горизонт предстоящих событий, дней"
                  min={0}
                  max={365}
                  {...form.getInputProps('upcomingEventDaysThreshold')}
                />
                <NumberInput
                  label="Порог свободных денег на счёте, ₽"
                  min={0}
                  {...form.getInputProps('uninvestedCashThresholdRub')}
                />
                <NumberInput
                  label="Максимальная концентрация по эмитенту, %"
                  min={0}
                  max={100}
                  {...form.getInputProps('defaultMaxConcentrationPercent')}
                />
              </SimpleGrid>
              <Group justify="flex-end" mt="lg">
                <Button type="submit" loading={savingThresholds} data-testid="thresholds-save">
                  Сохранить пороги
                </Button>
              </Group>
            </form>
          </Section>
        </>
      )}

      <Section
        title="Данные эмитента"
        description="Рейтинги агентств и отчётность, введённые вручную, учитываются в скоркарте надёжности (бесплатного источника рейтингов нет; у банков отчётность в ГИР БО скрыта)"
        data-testid="issuer-data-section"
      >
        <IssuerDataBlock />
      </Section>
    </Page>
  );
}
