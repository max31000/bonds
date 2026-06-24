import { Container, Stack, Title, Text, Badge, Paper, Button } from '@mantine/core';
import { useAuthStore } from '../store/useAuthStore';
import { useIsInsideShell } from '../hooks/useIsInsideShell';

/**
 * Заглушка этапа 01 — "walking skeleton". Доменные дашборды появятся в этапе 09.
 * Кнопка «Выйти» показывается только в standalone-режиме (внутри iframe portal-shell
 * выход из сессии будет управляться шеллом — см. этап 09).
 */
export function ComingSoon() {
  const logout = useAuthStore((s) => s.logout);
  const insideShell = useIsInsideShell();

  return (
    <Container size="sm" py="xl">
      <Stack align="center" gap="md" mt="20vh">
        <Badge color="violet" size="lg" variant="light">
          MVP в разработке
        </Badge>
        <Title order={1} ta="center">
          Bond Portfolio Analytics
        </Title>
        <Text c="dimmed" ta="center" maw={480}>
          Аналитика облигационного портфеля: НКД, YTM, дюрация, G-спред и
          персональный календарь денежного потока. Сервис скоро заработает.
        </Text>
        <Paper withBorder p="md" radius="md" maw={480}>
          <Text size="sm" c="dimmed" ta="center">
            Все расчёты в этом сервисе — аналитические оценки, а не
            инвестиционные рекомендации.
          </Text>
        </Paper>
        {!insideShell && (
          <Button variant="subtle" color="gray" onClick={logout}>
            Выйти
          </Button>
        )}
      </Stack>
    </Container>
  );
}
