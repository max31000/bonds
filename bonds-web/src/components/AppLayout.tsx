import { AppShell, Group, Title, NavLink, Button, Badge, Tooltip, Text } from '@mantine/core';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useEffect } from 'react';
import { notifications } from '@mantine/notifications';
import dayjs from 'dayjs';
import relativeTime from 'dayjs/plugin/relativeTime';
import 'dayjs/locale/ru';
import { useAuthStore } from '../store/useAuthStore';
import { useSignalsStore } from '../store/useSignalsStore';
import { useSyncStore } from '../store/useSyncStore';

dayjs.extend(relativeTime);
dayjs.locale('ru');

interface NavItem {
  label: string;
  path: string;
  /** Экран ещё не реализован (09b/09c) — пункт навигации заведён заранее, чтобы не блокировать структуру. */
  comingSoon?: boolean;
  showUnreadBadge?: boolean;
}

const NAV_ITEMS: NavItem[] = [
  { label: 'Обзор', path: '/' },
  { label: 'Позиции', path: '/positions' },
  { label: 'Денежный поток', path: '/cashflow' },
  { label: 'Аналитика', path: '/analytics' },
  { label: 'Рекомендации', path: '/recommendations' },
  { label: 'Сигналы', path: '/signals', showUnreadBadge: true },
  { label: 'Настройки', path: '/settings' },
];

/**
 * Компактный индикатор здоровья синка рядом с кнопкой «Обновить данные» (plan/13 часть B) —
 * иначе упавший/деградировавший автосинк никак не виден в UI, и пользователь узнаёт об этом
 * только по пустым/устаревшим данным. Три состояния, приоритет сверху вниз:
 * 1. Оранжевый бейдж «Токен не подключён/недействителен» (TokenMissingOrInvalid) — кликабельный,
 *    ведёт на /settings, т.к. это единственное действие, которое решает проблему.
 * 2. Красный бейдж «Ошибка синка» с tooltip первой ошибки — когда последний прогон завершился
 *    позже последнего успеха (LastFailureAtUtc > LastSuccessAtUtc).
 * 3. Зелёная точка + «синк N назад» — здоровое состояние.
 */
function SyncHealthIndicator() {
  const navigate = useNavigate();
  const status = useSyncStore((s) => s.status);

  if (!status) return null;

  if (status.tokenMissingOrInvalid) {
    return (
      <Badge
        color="orange"
        variant="filled"
        style={{ cursor: 'pointer' }}
        onClick={() => navigate('/settings')}
        data-testid="sync-health-token-badge"
      >
        Токен не подключён / недействителен
      </Badge>
    );
  }

  const lastFailure = status.lastFailureAtUtc ? dayjs(status.lastFailureAtUtc) : null;
  const lastSuccess = status.lastSuccessAtUtc ? dayjs(status.lastSuccessAtUtc) : null;
  const hasUnresolvedFailure = lastFailure !== null && (lastSuccess === null || lastFailure.isAfter(lastSuccess));

  if (hasUnresolvedFailure) {
    return (
      <Tooltip label={status.lastRunErrors[0] ?? 'Подробности — в логах сервера.'}>
        <Badge color="red" variant="filled" data-testid="sync-health-error-badge">
          Ошибка синка
        </Badge>
      </Tooltip>
    );
  }

  if (lastSuccess) {
    return (
      <Group gap={6} data-testid="sync-health-ok">
        <span
          style={{
            display: 'inline-block',
            width: 8,
            height: 8,
            borderRadius: '50%',
            backgroundColor: 'var(--mantine-color-green-6)',
          }}
        />
        <Text size="sm" c="dimmed">
          синк {lastSuccess.fromNow()}
        </Text>
      </Group>
    );
  }

  return null;
}

/**
 * Каркас приложения: шапка + боковая навигация (Mantine AppShell).
 * Рендерится всегда, в том числе внутри iframe portal-shell: ServiceFrame шелла —
 * это просто <iframe>, без собственной под-навигации сервиса и без управления его
 * сессией (см. portal-shell/src/components/ServiceFrame.tsx), поэтому без этой шапки
 * пользователь не смог бы дойти до «Настройки» или нажать «Обновить данные».
 * Паттерн соответствует cashpulse-web/AppLayout.tsx (тоже без insideShell-ветки).
 */
/** Plan/16 часть B: порог "долгого перерыва" — старше этого с последнего успешного синка триггерит тихий фоновый синк при открытии. */
const STALE_SYNC_THRESHOLD_MS = 12 * 60 * 60 * 1000;

export function AppLayout() {
  const navigate = useNavigate();
  const location = useLocation();
  const logout = useAuthStore((s) => s.logout);
  const signals = useSignalsStore((s) => s.signals);
  const loadSignals = useSignalsStore((s) => s.load);
  const { isRunning, triggerSync, refreshStatus } = useSyncStore();

  const unreadCount = signals.filter((s) => !s.isRead).length;

  useEffect(() => {
    loadSignals();

    // Plan/16 часть B: автосинк при открытии приложения после долгого перерыва — если последний
    // успешный синк старше 12 часов и прямо сейчас ничего не бежит, тихо дёргаем force-sync (без
    // модалок; уведомление придёт по завершении через существующий тост triggerSync/handleSync).
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

  const handleSync = async () => {
    const result = await triggerSync();
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
        message: `Инструментов: ${result.instrumentsSynced}, операций: ${result.operationsUpserted}, сигналов создано: ${result.signalsCreated}.`,
      });
    }
    loadSignals();
  };

  return (
    <AppShell header={{ height: 60 }} navbar={{ width: 240, breakpoint: 'sm' }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Title order={3} c="violet">
            Bond Portfolio Analytics
          </Title>
          <Group>
            <SyncHealthIndicator />
            <Button
              variant="light"
              loading={isRunning}
              onClick={handleSync}
              data-testid="force-sync-button"
            >
              Обновить данные
            </Button>
            <Button variant="subtle" color="gray" onClick={logout}>
              Выйти
            </Button>
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="md">
        {NAV_ITEMS.map((item) => (
          <NavLink
            key={item.path}
            label={item.label}
            active={location.pathname === item.path}
            onClick={() => navigate(item.path)}
            rightSection={
              item.comingSoon ? (
                <Badge size="xs" color="gray" variant="light">
                  скоро
                </Badge>
              ) : item.showUnreadBadge && unreadCount > 0 ? (
                <Badge size="xs" color="violet" variant="filled">
                  {unreadCount}
                </Badge>
              ) : undefined
            }
          />
        ))}
      </AppShell.Navbar>

      <AppShell.Main>
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
