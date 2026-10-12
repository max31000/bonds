import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';

// Mantine first, then the kit (tokens, base, Mantine overrides).
import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dates/styles.css';
import '@mvv42/kit/styles.css';
// Bonds-specific styles last: they build on (and in one place deliberately override) the kit.
import './app.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
