import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ExchangePage } from './ExchangePage';
import { UsersPanel } from './UsersPanel';
import { installFetch, renderPage } from '@/test/utils';

const batch = {
  id: 'b1', partner: 'Сбербанк', when: '01.11.2026 10:00', title: 'Загрузка Сбербанк', fileName: 'sber.csv', keyColumn: 'ЛС',
  total: 1, sum: 100050, posted: false,
  lines: [{ id: 'l1', line: 1, key: '40010003', amount: 100050, state: 'ok' }],
};

describe('Обмен — загрузка и проведение реестра', () => {
  it('загружает файл, показывает партию и проводит её', async () => {
    const mock = installFetch({ 'GET /exchange/batches': [], 'GET /exchange/operations': [], 'POST /exchange/upload': batch, 'POST /exchange/batches/b1/post': { ...batch, posted: true } });
    const { container } = renderPage(<ExchangePage />, '/exchange');

    // после загрузки список партий уже содержит созданную
    mock.set('GET /exchange/batches', [batch]);
    const input = await screen.findByText(/CSV или TXT/);
    expect(input).toBeInTheDocument();
    const fileInput = container.querySelector('input[type="file"]') as HTMLInputElement;
    const user = userEvent.setup();
    await user.upload(fileInput, new File(['40010003;1000,50'], 'sber.csv', { type: 'text/csv' }));

    const postButton = await screen.findByRole('button', { name: /Провести строк: 1/ });
    mock.set('GET /exchange/batches', [{ ...batch, posted: true }]);
    await user.click(postButton);

    expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/exchange/batches/b1/post')).toBe(true);
    expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/exchange/upload')).toBe(true);
  });
});

describe('Админ — создание сотрудника', () => {
  it('создаёт пользователя и показывает временный пароль', async () => {
    const mock = installFetch({ 'GET /admin/users': [], 'POST /admin/users': { login: 'petr', temporaryPassword: 'ABC12345' } });
    renderPage(<UsersPanel />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Новый сотрудник' }));
    await user.type(screen.getByLabelText('Имя'), 'Пётр Петров');
    await user.type(screen.getByLabelText('Логин'), 'petr');
    await user.click(screen.getByRole('button', { name: 'Создать и выдать пароль' }));

    expect(await screen.findByText(/Временный пароль для petr/)).toBeInTheDocument();
    expect(screen.getByText('ABC12345')).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/admin/users');
    expect(call?.body).toMatchObject({ login: 'petr', displayName: 'Пётр Петров', role: 'cashier' });
  });
});
