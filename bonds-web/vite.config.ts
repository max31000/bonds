import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { mvv42Html } from '@mvv42/kit/vite';

// https://vite.dev/config/
export default defineConfig({
  base: '/bonds/',
  // mvv42Html(): anti-flash colour-scheme script (shared `mvv42-color-scheme` key) in <head>.
  plugins: [react(), mvv42Html()],
  resolve: {
    // One copy of the singletons the kit shares with the app (contexts, theme store, notifications).
    dedupe: ['react', 'react-dom', '@mantine/core', '@mantine/hooks', '@mantine/notifications'],
  },
  server: {
    port: 5174,
    // Same-origin like prod: the shared-auth cookie (`mvv42_at` in Development) is sent to the API only when
    // the browser talks to one origin. API: `dotnet run` (appsettings.Development: :5001); auth: mvv42-auth
    // `dotnet run` (:5010). The page origin (http://localhost:5174) is in Mvv42Auth:AllowedOrigins.
    proxy: {
      '/bonds/api': { target: 'http://localhost:5001', rewrite: (path) => path.replace(/^\/bonds\/api/, '/api') },
      '/auth': { target: 'http://localhost:5010' },
    },
  },
});
