import { Button } from '@mantine/core';
import { EmptyState, Page } from '@mvv42/kit';
import { useNavigate } from 'react-router-dom';

/** Неизвестный путь внутри сервиса. */
export function NotFound() {
  const navigate = useNavigate();
  return (
    <Page>
      <EmptyState
        title="Такой страницы нет"
        description="Возможно, ссылка устарела. Вернитесь к обзору портфеля."
        action={
          <Button variant="default" onClick={() => navigate('/')}>
            К обзору
          </Button>
        }
      />
    </Page>
  );
}
