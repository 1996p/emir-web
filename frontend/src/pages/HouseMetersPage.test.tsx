import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HouseMetersPage } from './HouseMetersPage';
import { installFetch, renderPage } from '@/test/utils';

const meters = {
  period: '2026-11',
  rows: [{ id: 'hm1', place: 'рп. Чистоозерное', street: 'ул. Пархоменко 1', number: '1', unit: 'ГДж', unitFactor: 0.239, previous: 87, value: null, lines: 2 }],
};

describe('Общедомовые счётчики — массовая привязка квартир', () => {
  it('открывает панель, показывает кандидатов и привязывает выбранных', async () => {
    const mock = installFetch({
      'GET /readings/house-meters': meters,
      'GET /readings/house-meters/hm1/candidates': {
        meter: 'рп. Чистоозерное, ул. Пархоменко 1, № 1, ГДж',
        chargedThisMonth: false,
        candidates: [
          { key: 'a1:heat_meter', abonent: 'Иванов И.', flat: '1', service: 'Теплоснабжение по счетчику', linkedToId: null, linkedToName: null },
        ],
      },
      'POST /readings/house-meters/hm1/link': { changed: 1 },
    });
    renderPage(<HouseMetersPage />, '/readings/house');

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Квартиры' }));

    const panel = await screen.findByLabelText('Привязка квартир к счётчику');
    expect(within(panel).getByText(/кв\. 1/)).toBeInTheDocument();
    expect(within(panel).getByText(/не привязана/)).toBeInTheDocument();

    await user.click(within(panel).getByRole('button', { name: 'Выбрать непривязанные' }));
    await user.type(within(panel).getByLabelText('Основание'), 'акт ОДПУ');
    await user.click(within(panel).getByRole('button', { name: 'Привязать выбранные' }));

    expect(await screen.findByText(/Привязано услуг: 1/)).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/readings/house-meters/hm1/link');
    expect(call?.body).toMatchObject({ keys: ['a1:heat_meter'], link: true, reason: 'акт ОДПУ' });
  });

  it('блокирует привязку, если по счётчику уже есть начисления месяца', async () => {
    installFetch({
      'GET /readings/house-meters': meters,
      'GET /readings/house-meters/hm1/candidates': {
        meter: 'рп. Чистоозерное, ул. Пархоменко 1, № 1, ГДж',
        chargedThisMonth: true,
        candidates: [{ key: 'a1:heat_meter', abonent: 'Иванов И.', flat: '1', service: 'Теплоснабжение по счетчику', linkedToId: 'hm1', linkedToName: null }],
      },
    });
    renderPage(<HouseMetersPage />, '/readings/house');

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Квартиры' }));
    const panel = await screen.findByLabelText('Привязка квартир к счётчику');
    expect(within(panel).getByText(/уже есть начисления/)).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Привязать выбранные' })).toBeDisabled();
  });
});
