import { describe, it, expect, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CommandPalette } from './CommandPalette';
import { installFetch, renderPage } from '@/test/utils';

const person = { id: 'a1', kind: 'person', name: 'Иванова Анна', shortName: 'Иванова А.', place: 'рп. Ч', street: 'ул. Ленина 1', flat: '2', account: '40010001', residents: 1, area: 50, credit: 0, services: [] };

describe('Общий поиск (CommandPalette)', () => {
  it('показывает действия и абонентов, фильтрует по запросу', async () => {
    installFetch({ 'GET /abonents': [person] });
    renderPage(<CommandPalette onClose={() => {}} />);

    expect(await screen.findByText('Принять оплату')).toBeInTheDocument();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Что найти'), 'Долж');
    expect(await screen.findByText('Должники')).toBeInTheDocument();
    expect(screen.queryByText('Принять оплату')).not.toBeInTheDocument();
  });

  it('находит абонента по имени', async () => {
    installFetch({ 'GET /abonents': [person] });
    renderPage(<CommandPalette onClose={() => {}} />);
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Что найти'), 'Иванова');
    expect(await screen.findByText('Иванова Анна')).toBeInTheDocument();
  });

  it('закрывается по Escape и по клику на строку', async () => {
    const onClose = vi.fn();
    installFetch({ 'GET /abonents': [] });
    renderPage(<CommandPalette onClose={onClose} />);
    const user = userEvent.setup();

    const input = await screen.findByLabelText('Что найти');
    await user.type(input, '{Escape}');
    expect(onClose).toHaveBeenCalledTimes(1);

    await user.click(screen.getByText('Принять оплату'));
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
