import { MantineProvider, ColorSchemeScript } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { theme } from './theme';
import { ComingSoon } from './pages/ComingSoon';
import { useShellSync } from './hooks/useShellSync';

import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dates/styles.css';

/** Компонент внутри Router — может использовать хуки react-router-dom */
function AppRoutes() {
  useShellSync('bonds');

  return (
    <Routes>
      <Route index element={<ComingSoon />} />
      <Route path="*" element={<ComingSoon />} />
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
