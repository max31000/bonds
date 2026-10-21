import { useEffect } from 'react';
import { Badge, Button, Group, Text, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { ErrorState, LoginRequired } from '@mvv42/kit';
import { AUTH_CHANGED_EVENT, onShellMessage, useMe } from '@mvv42/kit/platform';
import { AppLayout, type NavItem } from '@mvv42/kit/router';
import { IconChartCandle, IconRefresh } from '@tabler/icons-react';
import { Outlet, useNavigate } from 'react-router-dom';
import dayjs from 'dayjs';
import relativeTime from 'dayjs/plugin/relativeTime';
import 'dayjs/locale/ru';
import { resetAuthState, SERVICE_ID } from '../api/client';
import { useAccessStore } from '../store/useAccessStore';
import { countAttention, useSignalsStore } from '../store/useSignalsStore';
import { useSyncStore } from '../store/useSyncStore';

dayjs.extend(relativeTime);
dayjs.locale('ru');

interface BondsNavItem {
  label: string;
  path: string;
  showUnreadBadge?: boolean;
}

const NAV_ITEMS: BondsNavItem[] = [
  { label: 'Обзор', path: '/' },
  { label: 'Позиции', path: '/positions' },
  { label: 'Доходы', path: '/income' },
  { label: 'Денежный поток', path: '/cashflow' },
  { label: 'Аналитика', path: '/analytics' },
  { label: 'Рекомендации', path: '/recommendations' },
  { label: 'Скринер', path: '/screener' },
  { label: 'События', path: '/signals', showUnreadBadge: true },
  { label: 'Настройки', path: '/settings' },
];

/** Порог "долгого перерыва" — старше этого с последнего успешного синка триггерит тихий фоновый синк при открытии. */
const STALE_SYNC_THRESHOLD_MS = 12 * 60 * 60 * 1000;

/** Пользователь вошёл и `/auth/api/me` выдаёт ему bonds (окончательно решает API: 403 → «Нет доступа»). */
function useHasAccess(): boolean {
  const { user, app } = useMe();
  return user !== null && app(SERVICE_ID)?.granted === true;
}

/**
 * Компактный индикатор здоровья синка рядом с кнопкой «Обновить данные» —
 * иначе упавший/деградировавший автосинк никак не виден в UI. Три состояния, приоритет сверху вниз:
 * 1. Бейдж «Токен не подключён» (TokenMissingOrInvalid) — кликабельный, ведёт на /settings.
 * 2. Бейдж «Ошибка синка» с tooltip первой ошибки — последний прогон упал позже последнего успеха.
 * 3. Точка + «синк N назад» — здоровое состояние.
 */
export function SyncHealthIndicator() {
  const navigate = useNavigate();
  const status = useSyncStore((s) => s.status);

  if (!status) return null;

  if (status.tokenMissingOrInvalid) {
    return (
      <Tooltip label="Токен T-Invest не подключён или недействителен — откройте настройки">
        <Badge
          component="button"
          type="button"
          color="orange"
          onClick={() => navigate('/settings')}
          style={{ cursor: 'pointer' }}
          data-testid="sync-health-token-badge"
        >
          Токен не подключён / недействителен
        </Badge>
      </Tooltip>
    );
  }

  const lastFailure = status.lastFailureAtUtc ? dayjs(status.lastFailureAtUtc) : null;
  const lastSuccess = status.lastSuccessAtUtc ? dayjs(status.lastSuccessAtUtc) : null;
  const hasUnresolvedFailure = lastFailure !== null && (lastSuccess === null || lastFailure.isAfter(lastSuccess));

  if (hasUnresolvedFailure) {
    return (
      <Tooltip label={status.lastRunErrors[0] ?? 'Подробности — в логах сервера.'}>
        <Badge color="red" data-testid="sync-health-error-badge">
          Ошибка синка
        </Badge>
      </Tooltip>
    );
  }

  if (lastSuccess) {
    return (
      <Group gap={6} wrap="nowrap" data-testid="sync-health-ok" className="bonds-sync-ok">
        <span className="bonds-status-dot" aria-hidden="true" />
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
          синк {lastSuccess.fromNow()}
        </Text>
      </Group>
    );
  }

  return null;
}

function showSyncResult(result: Awaited<ReturnType<ReturnType<typeof useSyncStore.getState>['triggerSync']>>) {
  if (!result) return;
  if (result.hasErrors) {
    notifications.show({
      color: 'red',
      title: 'Обновление завершилось с ошибками',
      message: result.errors[0] ?? 'Подробности — в логах сервера.',
    });
  } else {
    notifications.show({
      color: 'green',
      title: 'Данные обновлены',
      message: `Инструментов: ${result.instrumentsSynced}, операций: ${result.operationsUpserted}, новых событий: ${result.signalsCreated}.`,
    });
  }
}

/** Действия в шапке: индикатор синка и «Обновить данные». */
export function SyncActions() {
  const isRunning = useSyncStore((s) => s.isRunning);
  const triggerSync = useSyncStore((s) => s.triggerSync);
  const loadSignals = useSignalsStore((s) => s.load);

  const handleSync = async () => {
    const result = await triggerSync();
    showSyncResult(result);
    if (result) loadSignals();
  };

  return (
    <Group gap="sm" wrap="nowrap">
      <SyncHealthIndicator />
      <Button
        variant="default"
        size="xs"
        leftSection={<IconRefresh size={14} />}
        loading={isRunning}
        onClick={handleSync}
        data-testid="force-sync-button"
      >
        Обновить данные
      </Button>
    </Group>
  );
}

/**
 * Загрузка данных шапки (сигналы для бейджа, статус синка) и автосинк после долгого перерыва
 *: если последний успешный синк старше 12 часов и ничего не бежит — тихо
 * запускаем синк, по завершении — тост. Рендерится только внутри `LoginRequired`.
 */
function SessionBootstrap() {
  const loadSignals = useSignalsStore((s) => s.load);
  const refreshStatus = useSyncStore((s) => s.refreshStatus);
  const triggerSync = useSyncStore((s) => s.triggerSync);

  useEffect(() => {
    loadSignals();

    void (async () => {
      await refreshStatus();
      const status = useSyncStore.getState();
      if (status.isRunning) return;

      const lastSuccessAtUtc = status.status?.lastSuccessAtUtc;
      const isStale =
        lastSuccessAtUtc === null ||
        lastSuccessAtUtc === undefined ||
        Date.now() - new Date(lastSuccessAtUtc).getTime() > STALE_SYNC_THRESHOLD_MS;

      if (!isStale) return;

      const result = await triggerSync();
      if (!result) return;

      notifications.show({
        color: result.hasErrors ? 'red' : 'green',
        title: 'Фоновое обновление данных',
        message: result.hasErrors
          ? (result.errors[0] ?? 'Автоматическое обновление завершилось с ошибками.')
          : `Данные обновлены после долгого перерыва (инструментов: ${result.instrumentsSynced}, операций: ${result.operationsUpserted}).`,
      });
      loadSignals();
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return null;
}

function Content() {
  const denied = useAccessStore((s) => s.denied);
  if (denied !== null) {
    return (
      <div className="bonds-gate">
        <ErrorState
          title="Нет доступа"
          error={denied}
          description="Bond Analytics — личный сервис владельца портфеля. Ваш аккаунт вошёл, но доступа к данным у него нет."
          data-testid="access-denied"
        />
      </div>
    );
  }
  return (
    <>
      <SessionBootstrap />
      <Outlet />
    </>
  );
}

/**
 * Каркас сервиса на ките: `AppLayout` (standalone — верхний бар mvv42 / Bond Analytics с темой и
 * аккаунтом; внутри shell — только полоса разделов) + весь контент за `LoginRequired`.
 * Навигация и действия шапки показываются только пользователю с доступом.
 */
/**
 * Сброс «Нет доступа» и флага «вход уже запрошен», когда меняется сессия: другой пользователь в `/me`
 * (вошёл/вышел/сменил аккаунт) или `AUTH_CHANGED` от shell / локального logout.
 */
function useResetOnSessionChange() {
  const userId = useMe().user?.id ?? null;
  useEffect(() => {
    resetAuthState();
  }, [userId]);
  useEffect(() => {
    const off = onShellMessage((m) => {
      if (m.type === 'AUTH_CHANGED') resetAuthState();
    });
    window.addEventListener(AUTH_CHANGED_EVENT, resetAuthState);
    return () => {
      off();
      window.removeEventListener(AUTH_CHANGED_EVENT, resetAuthState);
    };
  }, []);
}

export function ServiceLayout() {
  useResetOnSessionChange();
  const hasAccess = useHasAccess();
  const denied = useAccessStore((s) => s.denied);
  const unreadCount = useSignalsStore((s) => countAttention(s.signals));

  const nav: NavItem[] =
    hasAccess && !denied
      ? NAV_ITEMS.map((item) => ({
          label: item.label,
          to: item.path,
          badge: item.showUnreadBadge && unreadCount > 0 ? unreadCount : undefined,
        }))
      : [];

  return (
    <AppLayout
      title="Bond Analytics"
      icon={<IconChartCandle size={16} stroke={1.8} />}
      nav={nav}
      actions={hasAccess && !denied ? <SyncActions /> : undefined}
    >
      <LoginRequired description="Аналитика облигационного портфеля доступна после входа через Telegram.">
        <Content />
      </LoginRequired>
    </AppLayout>
  );
}
