import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AccountPage } from './AccountPage';
import { DirectoriesPage } from './DirectoriesPage';
import { installFetch, renderPage } from '@/test/utils';

describe('Профиль и смена пароля', () => {
  it('предупреждает о несовпадении паролей', async () => {
    installFetch({});
    renderPage(<AccountPage />, '/account');
    expect(await screen.findByText('Сменить пароль')).toBeInTheDocument();

    const user = userEvent.setup();
    const fields = screen.getAllByLabelText(/пароль/i);
    // поля: текущий, новый, повтор — заполняем новый и повтор по-разному
    await user.type(fields[fields.length - 2]!, 'newpass-1');
    await user.type(fields[fields.length - 1]!, 'newpass-2');
    expect(await screen.findByText('Пароли не совпадают.')).toBeInTheDocument();
  });
});

describe('Справочники (оболочка)', () => {
  it('показывает дерево справочников и таблицу выбранного', async () => {
    installFetch({
      'GET /directories/groups': [{ name: 'Люди и организации', items: ['Люди'] }],
      'GET /directories/table': { columns: ['ЛС', 'ФИО'], rows: [['40010001', 'Иванова Анна']], sensitive: true, editable: false },
    });
    renderPage(<DirectoriesPage />, '/directories');
    expect(await screen.findByPlaceholderText('Найти справочник')).toBeInTheDocument();
    expect((await screen.findAllByText('Иванова Анна')).length).toBeGreaterThan(0);
  });
});
