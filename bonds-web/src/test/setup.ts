import '@testing-library/jest-dom/vitest';
import { beforeAll, afterEach, afterAll } from 'vitest';
import { notifications } from '@mantine/notifications';
import { server } from './msw-handlers';

// jsdom не реализует matchMedia — Mantine использует его для color scheme detection.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
});

// jsdom не реализует ResizeObserver — используется Mantine ScrollArea/Table.ScrollContainer.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
Object.defineProperty(window, 'ResizeObserver', {
  writable: true,
  value: ResizeObserverStub,
});

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  // Флейк «torn down timer» (ловили трижды): Mantine-тосты (notifications.show в AppLayout/
  // Settings и др.) держат таймер автозакрытия ~4с; под нагрузкой полного прогона он стреляет
  // после teardown тест-файла и роняет сюиту, хотя все ассерты прошли. Чистим очередь после
  // каждого теста — таймеры снимаются вместе с уведомлениями.
  notifications.clean();
  notifications.cleanQueue();
});
afterAll(() => server.close());
