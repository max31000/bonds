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
  },
});
