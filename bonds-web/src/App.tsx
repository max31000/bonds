import { useEffect } from 'react';
import { MantineProvider, ColorSchemeScript } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { theme } from './theme';
import { ComingSoon } from './pages/ComingSoon';
import { Dashboard } from './pages/Dashboard';
import { Positions } from './pages/Positions';
import { PositionDetail } from './pages/PositionDetail';
import { Cashflow } from './pages/Cashflow';
import { Analytics } from './pages/Analytics';
import { Recommendations } from './pages/Recommendations';
import { Signals } from './pages/Signals';
import { Settings } from './pages/Settings';
import Login from './pages/Login';
import { ProtectedRoute } from './components/ProtectedRoute';
import { AppLayout } from './components/AppLayout';
import { useShellSync } from './hooks/useShellSync';
import { useAuthStore } from './store/useAuthStore';
import { fetchMe } from './api/auth';

import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dates/styles.css';

/** Компонент внутри Router — может использовать хуки react-router-dom */
function AppRoutes() {
  useShellSync('bonds');

  // Проверка сессии на старте приложения: если токен невалиден/истёк,
  // GET /api/auth/me вернёт 401 → apiClient разлогинит и уведёт на /login.
  const token = useAuthStore((s) => s.token);
  const logout = useAuthStore((s) => s.logout);
  useEffect(() => {
    if (!token) return;
    fetchMe(token).catch(() => logout());
  }, [token, logout]);

  return (
    <Routes>
      <Route path="login" element={<Login />} />
      <Route element={<ProtectedRoute />}>
        <Route element={<AppLayout />}>
          <Route index element={<Dashboard />} />
          <Route path="positions" element={<Positions />} />
          <Route path="positions/:id" element={<PositionDetail />} />
          <Route path="cashflow" element={<Cashflow />} />
          <Route path="analytics" element={<Analytics />} />
          <Route path="recommendations" element={<Recommendations />} />
          <Route path="signals" element={<Signals />} />
          <Route path="settings" element={<Settings />} />
          <Route path="*" element={<ComingSoon />} />
        </Route>
      </Route>
    </Routes>
  );
}

export default function App() {
  return (
    <MantineProvider theme={theme} defaultColorScheme="auto">
      <ColorSchemeScript defaultColorScheme="auto" />
      <Notifications position="top-right" autoClose={5000} />
      <BrowserRouter basename={import.meta.env.BASE_URL}>
        <AppRoutes />
      </BrowserRouter>
    </MantineProvider>
  );
}
