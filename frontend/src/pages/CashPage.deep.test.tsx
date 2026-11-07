import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CashPage } from './CashPage';
import { installFetch, renderPage } from '@/test/utils';

type Svc = { id: string; name: string; state: string; tariffName: string; price: number | null; chargeStatus: string; debt: number; meter?: unknown };
const abonent = (over: Partial<{ id: string; name: string; account: string; credit: number; services: Svc[] }> = {}) => ({
  id: 'a1', kind: 'person', name: 'Иванова Анна', shortName: 'Иванова А.', place: 'рп. Чистоозерное', street: 'ул. Ленина 1', flat: '2',
  account: '40010001', residents: 1, area: 50, credit: 0,
  services: [{ id: 'heat', name: 'Теплоснабжение', state: 'Работает', tariffName: 'По нормативу', price: null, chargeStatus: '', debt: 10000 }],
  ...over,
});
const emptyShift = { entries: [], count: 0, total: 0 };

describe('Касса — оплата', () => {
  it('поиск → выбор абонента → оплата наличными «Ровно» проводит платёж', async () => {
    const mock = installFetch({
      'GET /abonents': [abonent()],
      'GET /cash/shift': emptyShift,
      'POST /cash/payments': { paymentId: 'p1', total: 10000, newBalance: 0 },
    });
    renderPage(<CashPage />, '/cash');

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Найти абонента'), 'Иванова');
    await user.keyboard('{Enter}');

    // форма оплаты абонента
    expect(await screen.findByRole('link', { name: 'Иванова Анна' })).toBeInTheDocument();
    expect(screen.getByText('Теплоснабжение')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ровно' }));
    await user.click(screen.getByRole('button', { name: /Провести 100,00/ }));

    expect(await screen.findByText('Оплата проведена')).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/cash/payments');
    expect(call?.body).toMatchObject({ abonentId: 'a1', source: 'cash-person', allocations: [{ serviceId: 'heat', amount: 10000 }] });
  });

  it('без принятой суммы кнопка просит ввести деньги', async () => {
    installFetch({ 'GET /abonents': [abonent()], 'GET /cash/shift': emptyShift });
    renderPage(<CashPage />, '/cash?id=a1');
    expect(await screen.findByRole('button', { name: 'Введите принятую сумму' })).toBeDisabled();
  });
});

describe('Касса — возврат', () => {
  it('выбирает платёж, требует причину и проводит возврат', async () => {
    const mock = installFetch({
      'GET /abonents': [abonent()],
      'GET /cash/shift': emptyShift,
      'GET /abonents/a1/payments': [{ id: 'pay1', abonentId: 'a1', date: '01.11.2026', source: 'Касса ФЛ', cashier: 'Кассир', amount: 5000, status: 'Проведена' }],
      'POST /cash/refunds': { amount: 5000, newBalance: -5000 },
    });
    renderPage(<CashPage />, '/cash?id=a1');

    const user = userEvent.setup();
    await user.click(await screen.findByText('Возврат'));
    // кнопка заблокирована, пока нет причины
    expect(await screen.findByRole('button', { name: 'Укажите причину' })).toBeDisabled();
    await user.type(screen.getByLabelText(/Причина возврата/), 'заплатили дважды');
    await user.click(screen.getByRole('button', { name: /Вернуть 50,00/ }));

    expect(await screen.findByText('Возврат выполнен')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/cash/refunds')).toBe(true);
  });
});

describe('Касса — пустой экран и смена', () => {
  it('без выбранного абонента показывает приглашение и смену', async () => {
    installFetch({
      'GET /abonents': [abonent()],
      'GET /cash/shift': { count: 2, total: 15000, entries: [{ id: 1, time: '10:00', name: 'Иванова', amount: 10000 }] },
    });
    renderPage(<CashPage />, '/cash');
    expect(await screen.findByText('Следующий абонент')).toBeInTheDocument();
    expect(screen.getByText(/Смена сегодня · 2/)).toBeInTheDocument();
  });
});
