import { AppShell, Group, Title, NavLink, Button, Badge } from '@mantine/core';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useIsInsideShell } from '../hooks/useIsInsideShell';
import { useAuthStore } from '../store/useAuthStore';

interface NavItem {
  label: string;
  path: string;
  /** Экран ещё не реализован (09b/09c) — пункт навигации заведён заранее, чтобы не блокировать структуру. */
  comingSoon?: boolean;
}

const NAV_ITEMS: NavItem[] = [
  { label: 'Позиции', path: '/' },
  { label: 'Денежный поток', path: '/cashflow' },
  { label: 'Аналитика', path: '/analytics' },
  { label: 'Сигналы', path: '/signals', comingSoon: true },
  { label: 'Настройки', path: '/settings', comingSoon: true },
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
          <Button variant="subtle" color="gray" onClick={logout}>
            Выйти
          </Button>
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
