import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RegisterPage } from './RegisterPage';
import { installFetch, renderPage } from '@/test/utils';

describe('Регистрация абонента', () => {
  it('показывает ошибку сервера про адрес без номера дома (guard)', async () => {
    const mock = installFetch({
      'GET /abonents': [],
      'POST /abonents': { status: 422, data: { detail: 'В адресе укажите улицу и номер дома, например: ул. Молодёжная 1' } },
    });
    renderPage(<RegisterPage />, '/abonents/new');

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('ФИО'), 'Иванов Иван');
    await user.type(screen.getByLabelText('Адрес'), 'абвгд');
    await user.click(screen.getByRole('button', { name: /Создать абонента/ }));

    expect(await screen.findByText(/укажите улицу и номер дома/)).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents');
    expect(call?.body).toMatchObject({ kind: 'person', name: 'Иванов Иван', address: 'абвгд' });
  });

  it('кнопка заблокирована, пока не заполнены имя и адрес', async () => {
    installFetch({ 'GET /abonents': [] });
    renderPage(<RegisterPage />, '/abonents/new');
    expect(await screen.findByRole('button', { name: /Заполните имя и адрес/ })).toBeDisabled();
  });
});
