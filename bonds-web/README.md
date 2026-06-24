# Bonds Web — Frontend

React 19 + TypeScript приложение, деплоится на VDS по субпути `/bonds/`.

## Технологии

- React 19 + TypeScript (strict)
- Mantine 9 (UI компоненты)
- Recharts (графики)
- Zustand (state management)
- React Router 7 (`basename = import.meta.env.BASE_URL`)
- Yarn 1.22 (`nodeLinker: node-modules`)
- Vite (`base: '/bonds/'`)

## Запуск

```bash
yarn install
yarn dev          # http://localhost:5174
yarn build        # production-сборка в dist/, ассеты с префиксом /bonds/
yarn typecheck
yarn test:run
```

Переменные окружения сборки: `VITE_API_BASE` (по умолчанию `/bonds/api`),
`VITE_TELEGRAM_BOT_USERNAME` (этап 02).
