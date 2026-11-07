import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App } from './App';
import { installFetch } from '@/test/utils';

const current = { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null };

describe('Приложение целиком', () => {
  it('после входа показывает оболочку с навигацией и экран «Сегодня»', async () => {
    installFetch({
      'GET /today': { tasks: [], actions: [], messages: [] },
      'GET /period/current': current,
      'GET /period/steps': [],
    }, 'admin');

    render(<MemoryRouter initialEntries={['/']}><App /></MemoryRouter>);

    // оболочка: пункты меню доступные администратору
    expect(await screen.findByRole('link', { name: 'Абоненты' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Админ' })).toBeInTheDocument();
    // индексный маршрут — экран «Сегодня»
    expect(await screen.findByText('Сегодня')).toBeInTheDocument();
  });

  it('кассиру не показывает раздел «Админ»', async () => {
    installFetch({
      'GET /today': { tasks: [], actions: [], messages: [] },
      'GET /period/current': current,
      'GET /period/steps': [],
    }, 'cashier');

    render(<MemoryRouter initialEntries={['/']}><App /></MemoryRouter>);

    expect(await screen.findByRole('link', { name: 'Касса' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Админ' })).not.toBeInTheDocument();
  });
});
