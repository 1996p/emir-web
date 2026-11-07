import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AbonentsPage } from './AbonentsPage';
import { installFetch, renderPage, type Routes } from '@/test/utils';

const person = {
  id: 'a1', kind: 'person', name: 'Иванова Анна Петровна', shortName: 'Иванова А. П.', place: 'рп. Чистоозерное', street: 'ул. Ленина 1', flat: '2',
  account: '40010001', phone: '+7 900', residents: 3, area: 52, credit: 0,
  services: [{ id: 'heat', name: 'Теплоснабжение', state: 'Работает', tariffName: 'По нормативу', price: null, chargeStatus: '', debt: 10000 }],
};
const details = { note: null, consent: null, consentWhen: null, ownershipDocument: null, phone: null, openPeriod: '2026-11', reopened: false, facts: [], residents: [], houseHeatNorm: null };
const month = { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null };

const base = (over: Routes = {}): Routes => ({
  'GET /abonents': [person],
  'GET /period/current': month,
  'GET /tariffs': { openPeriod: '2026-11', reopened: false, tariffs: [] },
  'GET /abonents/a1/payments': [], 'GET /abonents/a1/journal': [], 'GET /abonents/a1/charges': [], 'GET /abonents/a1/ledger': [], 'GET /abonents/a1/details': details,
  ...over,
});

describe('Карточка абонента — действия (ящики)', () => {
  it('«Начислить» пересчитывает месяц', async () => {
    const mock = installFetch(base({ 'POST /abonents/a1/recharge': { abonents: 1, lines: 1, total: 12345, issues: [] } }));
    renderPage(<AbonentsPage />, '/abonents?id=a1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Начислить' }));
    const chargeDrawer = within(await screen.findByRole('complementary', { name: 'Начислить абоненту' }));
    await user.click(await chargeDrawer.findByRole('button', { name: /Пересчитать/ }));

    expect(await screen.findByText(/Начислено 123,45/)).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/abonents/a1/recharge')).toBe(true);
  });

  it('«Сменить адрес» отправляет новый адрес', async () => {
    const mock = installFetch(base({ 'POST /abonents/a1/address': { ...person, street: 'ул. Мира 7' } }));
    renderPage(<AbonentsPage />, '/abonents?id=a1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Сменить адрес' }));
    const addressDrawer = within(await screen.findByRole('complementary', { name: 'Сменить адрес' }));
    await user.type(addressDrawer.getByLabelText('Новый адрес'), 'ул. Мира 7');
    await user.click(addressDrawer.getByRole('button', { name: 'Сменить адрес' }));

    expect(await screen.findByText('Адрес изменён.')).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents/a1/address');
    expect(call?.body).toMatchObject({ address: 'ул. Мира 7' });
  });

  it('«Добавить услугу» добавляет услугу', async () => {
    const mock = installFetch(base({ 'POST /abonents/a1/services': person }));
    renderPage(<AbonentsPage />, '/abonents?id=a1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Добавить услугу' }));
    const serviceDrawer = within(await screen.findByRole('complementary', { name: 'Добавить услугу' }));
    await user.click(serviceDrawer.getByRole('button', { name: 'Добавить услугу' }));

    expect(await screen.findByText(/Услуга «Горячая вода» добавлена/)).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/abonents/a1/services')).toBe(true);
  });
});
