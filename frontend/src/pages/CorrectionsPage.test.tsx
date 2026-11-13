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

  it('из карточки абонент закреплён: поле только для чтения', async () => {
    installFetch({
      'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
      'GET /corrections/types': types, 'GET /abonents': [person], 'GET /abonents/a1/payments': [],
    });
    renderPage(<CorrectionsPage />, '/corrections?id=a1');
    const field = await screen.findByLabelText('Абонент');
    expect(field).toHaveAttribute('readonly');
    expect(field).toHaveValue('Иванова Анна, ЛС 40010001');
    expect(screen.queryByRole('combobox')).toBeNull();
  });

  it('без абонента в ссылке: никто не подставлен, абонент выбирается из выпадающего списка', async () => {
    const other = { ...person, id: 'a2', name: 'Петров Пётр', account: '40010002' };
    const mock = installFetch({
      'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
      'GET /corrections/types': types, 'GET /abonents': [person, other],
      'GET /abonents/a1/payments': [], 'GET /abonents/a2/payments': [],
      'POST /corrections': other,
    });
    renderPage(<CorrectionsPage />, '/corrections');
    const user = userEvent.setup();

    const combo = await screen.findByRole('combobox', { name: 'Абонент' });
    expect(combo).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'Списание долга' }));
    await user.type(screen.getByLabelText(/Причина/), 'ошибка');
    expect(screen.getByRole('button', { name: 'Выберите абонента' })).toBeDisabled();

    await user.click(combo);
    expect(screen.getAllByRole('option')).toHaveLength(2);
    await user.type(combo, 'петров');
    expect(screen.getAllByRole('option')).toHaveLength(1);
    await user.click(screen.getByRole('option', { name: /Петров Пётр/ }));
    expect(combo).toHaveValue('Петров Пётр, ЛС 40010002');

    await user.click(screen.getByRole('button', { name: /Провести 500,00/ }));
    expect(await screen.findByText(/Проведено/)).toBeInTheDocument();
    expect(mock.calls.find((c) => c.method === 'POST' && c.path === '/corrections')?.body).toMatchObject({ abonentId: 'a2' });
  });

  it('перенос между адресами: без верного абонента провести нельзя, выбранный уходит на сервер', async () => {
    const other = { ...person, id: 'a2', name: 'Петров Пётр', account: '40010002' };
    const move = { id: 'move-address', name: 'Перенос между адресами', group: 'Платежи', description: '', amountLabel: 'Сумма переноса', balanceEffect: -1, defaultAmount: 2000, flow: [], needsPayment: true, actionVerb: 'Перенести' };
    const mock = installFetch({
      'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
      'GET /corrections/types': [...types, move], 'GET /abonents': [person, other],
      'GET /abonents/a1/payments': [{ id: 'p1', abonentId: 'a1', date: '01.11.2026', source: 'Касса ФЛ', cashier: 'Кассир', amount: 5000, status: 'Проведена' }],
      'POST /corrections': person,
    });
    renderPage(<CorrectionsPage />, '/corrections?id=a1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Перенос между адресами' }));
    await user.type(screen.getByLabelText(/Причина/), 'ошиблись адресом');
    expect(screen.getByRole('button', { name: 'Выберите верного абонента' })).toBeDisabled();

    const combo = screen.getByRole('combobox', { name: 'Верный абонент' });
    await user.click(combo);
    // Текущего абонента в списке нет
    expect(screen.getAllByRole('option')).toHaveLength(1);
    await user.click(screen.getByRole('option', { name: /Петров Пётр/ }));
    await user.click(screen.getByRole('button', { name: /Перенести 20,00/ }));

    expect(await screen.findByText(/Проведено/)).toBeInTheDocument();
    expect(mock.calls.find((c) => c.method === 'POST' && c.path === '/corrections')?.body).toMatchObject({ abonentId: 'a1', typeId: 'move-address', paymentId: 'p1', targetAbonentId: 'a2', amount: 2000 });
  });

  it('перенос между услугами: с услуги в переплату', async () => {
    const move = { id: 'move-service', name: 'Перенос между услугами', group: 'Платежи', description: '', amountLabel: 'Сумма переноса', balanceEffect: 0, defaultAmount: 3000, flow: [], needsPayment: false, actionVerb: 'Перенести' };
    const mock = installFetch({
      'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
      'GET /corrections/types': [...types, move], 'GET /abonents': [person], 'GET /abonents/a1/payments': [],
      'POST /corrections': person,
    });
    renderPage(<CorrectionsPage />, '/corrections?id=a1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Перенос между услугами' }));
    expect(screen.getByLabelText('Откуда')).toHaveValue('heat');
    expect(screen.getByLabelText('Куда')).toHaveValue('credit');
    await user.type(screen.getByLabelText(/Причина/), 'не та услуга');
    await user.click(screen.getByRole('button', { name: /Перенести 30,00/ }));

    expect(await screen.findByText(/Проведено/)).toBeInTheDocument();
    expect(mock.calls.find((c) => c.method === 'POST' && c.path === '/corrections')?.body).toMatchObject({ typeId: 'move-service', fromServiceId: 'heat', toServiceId: 'credit' });
  });
});
