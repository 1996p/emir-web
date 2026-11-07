import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CorrectionsPage } from './CorrectionsPage';
import { installFetch, renderPage } from '@/test/utils';

const types = [
  { id: 'cancel-payment', name: 'Отмена платежа', group: 'Платежи', description: '', amountLabel: 'Сумма', balanceEffect: -1, defaultAmount: 0, flow: [], needsPayment: true, actionVerb: 'Отменить' },
  { id: 'write-off', name: 'Списание долга', group: 'Баланс', description: '', amountLabel: 'Сумма', balanceEffect: 1, defaultAmount: 50000, flow: [], needsPayment: false, actionVerb: 'Провести' },
];
const person = {
  id: 'a1', kind: 'person', name: 'Иванова Анна', shortName: 'Иванова А.', place: 'рп. Чистоозерное', street: 'ул. Ленина 1', flat: '2',
  account: '40010001', residents: 1, area: 50, credit: 0,
  services: [{ id: 'heat', name: 'Теплоснабжение', state: 'Работает', tariffName: 'По нормативу', price: null, chargeStatus: '', debt: 100000 }],
};

describe('Корректировки', () => {
  it('проводит списание долга с причиной', async () => {
    const mock = installFetch({
      'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
      'GET /corrections/types': types,
      'GET /abonents': [person],
      'GET /abonents/a1/payments': [{ id: 'p1', abonentId: 'a1', date: '01.11.2026', source: 'Касса ФЛ', cashier: 'Кассир', amount: 5000, status: 'Проведена' }],
      'POST /corrections': person,
    });
    renderPage(<CorrectionsPage />, '/corrections?id=a1');

    // по умолчанию выбрана «Отмена платежа»
    expect(await screen.findByRole('heading', { name: 'Отмена платежа' })).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Списание долга' }));
    await user.type(screen.getByLabelText(/Причина/), 'ошибка начисления');
    await user.click(screen.getByRole('button', { name: /Провести 500,00/ }));

    expect(await screen.findByText(/Проведено/)).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/corrections');
    expect(call?.body).toMatchObject({ abonentId: 'a1', typeId: 'write-off', reason: 'ошибка начисления' });
  });
});
