import { AppShell, Group, Title, NavLink, Button, Badge } from '@mantine/core';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useEffect } from 'react';
import { notifications } from '@mantine/notifications';
import { useIsInsideShell } from '../hooks/useIsInsideShell';
import { useAuthStore } from '../store/useAuthStore';
import { useSignalsStore } from '../store/useSignalsStore';
import { useSyncStore } from '../store/useSyncStore';

interface NavItem {
  label: string;
  path: string;
  /** Экран ещё не реализован (09b/09c) — пункт навигации заведён заранее, чтобы не блокировать структуру. */
  comingSoon?: boolean;
  showUnreadBadge?: boolean;
}

const NAV_ITEMS: NavItem[] = [
  { label: 'Позиции', path: '/' },
  { label: 'Денежный поток', path: '/cashflow' },
  { label: 'Аналитика', path: '/analytics' },
  { label: 'Сигналы', path: '/signals', showUnreadBadge: true },
  { label: 'Настройки', path: '/settings' },
];

/**
 * Каркас приложения: шапка + боковая навигация (Mantine AppShell).
 * Внутри iframe portal-shell скрывает собственную навигацию/шапку — навигация между
 * сервисами и выход из сессии управляются шеллом (см. portal-shell/docs/service-routing-integration.md).
 */
export function AppLayout() {
  const insideShell = useIsInsideShell();
  const navigate = useNavigate();
  const location = useLocation();
  const logout = useAuthStore((s) => s.logout);
  const signals = useSignalsStore((s) => s.signals);
  const loadSignals = useSignalsStore((s) => s.load);
  const { isRunning, triggerSync, refreshStatus } = useSyncStore();

  const unreadCount = signals.filter((s) => !s.isRead).length;

  useEffect(() => {
    loadSignals();
    refreshStatus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleSync = async () => {
    const result = await triggerSync();
    if (!result) return;
    if (result.hasErrors) {
      notifications.show({
        color: 'red',
        title: 'Обновление завершилось с ошибками',
        message: `Инструментов: ${result.instrumentsSynced}, операций: ${result.operationsUpserted}. Подробности — в логах сервера.`,
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

  if (insideShell) {
    // В shell-режиме шапка/навигация сервиса избыточны — portal-shell предоставляет свои.
    return <Outlet />;
  }

  return (
    <AppShell header={{ height: 60 }} navbar={{ width: 240, breakpoint: 'sm' }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Title order={3} c="violet">
            Bond Portfolio Analytics
          </Title>
          <Group>
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
