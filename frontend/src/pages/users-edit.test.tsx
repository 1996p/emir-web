import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { UsersPanel } from './UsersPanel';
import { installFetch, renderPage } from '@/test/utils';

const user = { id: 1, login: 'buh1', displayName: 'Бухгалтер Иванова', role: 'accountant', isActive: true, mustChangePassword: false, locked: false, lastLogin: null };

describe('Админ — редактирование сотрудника', () => {
  it('сбрасывает пароль и показывает временный', async () => {
    const mock = installFetch({
      'GET /admin/users': [user],
      'POST /admin/users/1/reset-password': { login: 'buh1', temporaryPassword: 'TMP98765' },
    });
    renderPage(<UsersPanel />);
    const u = userEvent.setup();

    await u.click(await screen.findByText('Бухгалтер Иванова'));
    await u.click(await screen.findByRole('button', { name: 'Сбросить пароль' }));

    expect(await screen.findByText(/Временный пароль для buh1/)).toBeInTheDocument();
    expect(screen.getByText('TMP98765')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/admin/users/1/reset-password')).toBe(true);
  });
});
