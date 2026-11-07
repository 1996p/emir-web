import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { LoginPage } from './LoginPage';
import { installFetch, renderRaw } from '@/test/utils';

describe('Экран входа', () => {
  it('показывает ошибку при неверном пароле', async () => {
    installFetch({ 'POST /auth/login': { status: 401, data: { detail: 'Неверный логин или пароль' } } }, null);
    renderRaw(<LoginPage />);

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Логин'), 'admin');
    await user.type(screen.getByLabelText('Пароль'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByText('Неверный логин или пароль')).toBeInTheDocument();
  });

  it('отправляет логин без лишних пробелов', async () => {
    const mock = installFetch({ 'POST /auth/login': { login: 'admin', displayName: 'Администратор', role: 'admin', mustChangePassword: false } }, null);
    renderRaw(<LoginPage />);

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Логин'), '  admin  ');
    await user.type(screen.getByLabelText('Пароль'), 'secret-pass');
    await user.click(screen.getByRole('button', { name: 'Войти' }));

    const call = mock.calls.find((c) => c.path === '/auth/login');
    expect(call?.body).toMatchObject({ login: 'admin', password: 'secret-pass' });
  });
});
