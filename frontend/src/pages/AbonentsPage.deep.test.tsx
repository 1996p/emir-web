import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AbonentsPage } from './AbonentsPage';
import { installFetch, renderPage, type Routes } from '@/test/utils';

const person = {
  id: 'a1', kind: 'person', name: 'Иванова Анна Петровна', shortName: 'Иванова А. П.', place: 'рп. Чистоозерное', street: 'ул. Ленина 1', flat: '2',
  account: '40010001', phone: '+7 900 000', residents: 3, area: 52, credit: 0,
  services: [{ id: 'heat', name: 'Теплоснабжение', state: 'Работает', tariffName: 'По нормативу', price: 250000, chargeStatus: 'Ждёт начисления', debt: 10000 }],
};
const org = {
  id: 'a2', kind: 'org', name: 'ООО Ромашка', shortName: 'ООО Ромашка', place: 'рп. Чистоозерное', street: 'ул. Мира 5',
  account: '20000001', inn: '5400000000', residents: 0, area: 100, credit: 0,
  services: [{ id: 'heat', name: 'Теплоснабжение', state: 'Работает', tariffName: 'По счётчику', price: null, chargeStatus: '', debt: 0 }],
};
const details = { note: null, consent: null, consentWhen: null, ownershipDocument: null, phone: null, openPeriod: '2026-11', reopened: false, facts: [], residents: [], houseHeatNorm: null };
const tariffs = { openPeriod: '2026-11', reopened: false, tariffs: [{ id: 't1', name: 'Норматив', scheme: 'NormHouse', currentPrice: 250000, lines: 1, prices: [], unit: 'Гкал', serviceNorm: null, tariffNorm: null, penaltyEnabled: true }] };

const cardRoutes = (id: string, over: Routes = {}): Routes => ({
  [`GET /abonents/${id}/payments`]: [],
  [`GET /abonents/${id}/journal`]: [],
  [`GET /abonents/${id}/charges`]: [],
  [`GET /abonents/${id}/ledger`]: [],
  [`GET /abonents/${id}/details`]: details,
  ...over,
});
const base = (over: Routes = {}): Routes => ({
  'GET /abonents': [person, org],
  'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
  'GET /tariffs': tariffs,
  ...cardRoutes('a1'),
  ...cardRoutes('a2'),
  ...over,
});

describe('Абоненты — список и фильтры', () => {
  it('фильтрует по чипам «Долг» и «Юрлица»', async () => {
    installFetch(base());
    renderPage(<AbonentsPage />, '/abonents');

    expect(await screen.findByText('Найдено: 2')).toBeInTheDocument();
    expect(screen.getByText('Иванова А. П.')).toBeInTheDocument();
    expect(screen.getByText('ООО Ромашка')).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Долг' }));
    expect(await screen.findByText('Найдено: 1')).toBeInTheDocument();
    expect(screen.queryByText('ООО Ромашка')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Юрлица' }));
    expect(await screen.findByText('Найдено: 1')).toBeInTheDocument();
    expect(screen.getAllByText('ООО Ромашка').length).toBeGreaterThan(0);
  });

  it('ищет по строке запроса', async () => {
    installFetch(base());
    renderPage(<AbonentsPage />, '/abonents');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Поиск'), 'Мира');
    expect(await screen.findByText('Найдено: 1')).toBeInTheDocument();
  });
});

describe('Абоненты — виды списка', () => {
  it('переключается на «Дома» и «Карту»', async () => {
    installFetch(base());
    renderPage(<AbonentsPage />, '/abonents');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Дома' }));
    expect(await screen.findByRole('link', { name: 'Данные дома' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Карта' }));
    expect(await screen.findByText('Карта обслуживания')).toBeInTheDocument();
  });
});

describe('Абоненты — карточка', () => {
  it('показывает шапку, действия и переключает вкладки', async () => {
    installFetch(base({
      'GET /abonents/a1/payments': [{ id: 'p1', abonentId: 'a1', date: '01.11.2026', source: 'Касса ФЛ', cashier: 'Кассир 1', amount: 5000, status: 'Проведена' }],
      'GET /abonents/a1/journal': [{ id: 'j1', when: '01.11.2026', action: 'Смена адреса', who: 'Оператор', details: 'ул. Ленина 1', reason: 'заявление' }],
    }));
    renderPage(<AbonentsPage />, '/abonents?id=a1');

    // шапка карточки и мета
    expect(await screen.findByRole('heading', { name: 'Иванова Анна Петровна' })).toBeInTheDocument();
    expect(screen.getByText(/Физлицо · ЛС 40010001/)).toBeInTheDocument();
    // действия для админа
    expect(screen.getByRole('button', { name: 'Принять оплату' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Начислить' })).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: 'Оплаты' }));
    expect(await screen.findByText('Касса ФЛ')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Журнал' }));
    expect(await screen.findByText('Смена адреса')).toBeInTheDocument();
  });

  it('открывает ящик «Тариф услуги» с историей условий', async () => {
    installFetch(base({ 'GET /abonents/a1/services/heat/terms': [] }));
    renderPage(<AbonentsPage />, '/abonents?id=a1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Тариф услуги' }));
    expect(await screen.findByRole('button', { name: 'Сохранить условия' })).toBeInTheDocument();
    expect(screen.getByText('История')).toBeInTheDocument();
  });
});

const baseCashier = base;
describe('Абоненты — роль кассира', () => {
  it('кассиру не видны «Начислить» и «Добавить услугу»', async () => {
    installFetch(baseCashier(), 'cashier');
    renderPage(<AbonentsPage />, '/abonents?id=a1');
    expect(await screen.findByRole('button', { name: 'Принять оплату' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Начислить' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Добавить услугу' })).not.toBeInTheDocument();
  });
});
