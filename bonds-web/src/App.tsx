import { Mvv42Provider } from '@mvv42/kit';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { SERVICE_ID } from './api/client';
import { ServiceLayout } from './components/ServiceLayout';
import { Analytics } from './pages/Analytics';
import { Capital } from './pages/Capital';
import { Cashflow } from './pages/Cashflow';
import { Dashboard } from './pages/Dashboard';
import { FundDetail } from './pages/FundDetail';
import { Income } from './pages/Income';
import { NotFound } from './pages/NotFound';
import { PositionDetail } from './pages/PositionDetail';
import { Positions } from './pages/Positions';
import { Recommendations } from './pages/Recommendations';
import { Screener } from './pages/Screener';
import { Settings } from './pages/Settings';
import { Signals } from './pages/Signals';

/**
 * Корень: `Mvv42Provider` кита (тема с акцентом violet, общая цветовая схема, уведомления, мост с
 * shell) → роутер → `ServiceLayout` (AppLayout кита, весь контент за `LoginRequired`).
 * Вход — только общий mvv42-auth: своего логина, токенов и переключателя темы у сервиса нет.
 */
export default function App() {
  return (
    <Mvv42Provider serviceId={SERVICE_ID} accent="violet">
      <BrowserRouter basename={import.meta.env.BASE_URL}>
        <Routes>
          <Route element={<ServiceLayout />}>
            <Route index element={<Dashboard />} />
            <Route path="positions" element={<Positions />} />
            <Route path="positions/:id" element={<PositionDetail />} />
            <Route path="funds/:fundId" element={<FundDetail />} />
            <Route path="income" element={<Income />} />
            <Route path="capital" element={<Capital />} />
            <Route path="cashflow" element={<Cashflow />} />
            <Route path="analytics" element={<Analytics />} />
            <Route path="recommendations" element={<Recommendations />} />
            <Route path="screener" element={<Screener />} />
            <Route path="signals" element={<Signals />} />
            <Route path="settings" element={<Settings />} />
            <Route path="*" element={<NotFound />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </Mvv42Provider>
  );
}
