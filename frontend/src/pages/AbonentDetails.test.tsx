import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AbonentDetailsTab, AbonentNote } from './AbonentDetails';
import type { Abonent } from '@/domain/types';
import { installFetch, renderPage, type Routes } from '@/test/utils';

const abonent = {
  id: 'a1', kind: 'person', name: 'Иванова Анна Петровна', shortName: 'Иванова А. П.', place: 'рп. Чистоозерное', street: 'ул. Ленина 1', flat: '2',
  account: '40010001', phone: '+7 900', residents: 3, area: 52, credit: 0,
  services: [{ id: 'heat', name: 'Теплоснабжение', state: 'Работает', tariffName: 'По норме дома', price: null, chargeStatus: '', debt: 0 }],
} as unknown as Abonent;
const details = {
  note: null, consent: null, consentWhen: null, ownershipDocument: null, phone: '+7 900', openPeriod: '2026-11', reopened: false,
  facts: [{ fromPeriod: '2026-10', area: 52, residents: 3, who: 'Оператор', when: '01.10.2026', reason: 'ЕГРН' }],
  residents: [{ id: 1, name: 'Иванов Иван', fromDate: '01.01.2020', toDate: null }],
  houseHeatNorm: 0.01875,
};
const detailRoutes: Routes = {
  'GET /readings/house-meters': { period: '2026-11', rows: [] }, 'GET /benefits': [], 'GET /benefits/categories': [],
  'POST /abonents/a1/heat-norm': { status: 204 }, 'POST /abonents/a1/residents': { status: 204 }, 'POST /abonents/a1/facts': { status: 204 },
};
const section = (title: string) => screen.getByText(title).closest('section') as HTMLElement;

describe('Карточка абонента — вкладка «Жильцы и параметры»', () => {
  it('показывает все разделы с данными', async () => {
    installFetch(detailRoutes);
    renderPage(<AbonentDetailsTab abonent={abonent} details={details} />);

    expect(await screen.findByText('Параметры расчёта')).toBeInTheDocument();
    expect(screen.getByText('Проживающие: 1')).toBeInTheDocument();
    expect(screen.getByText('Иванов Иван')).toBeInTheDocument();
    expect(screen.getByText('Норматив потребления')).toBeInTheDocument();
    expect(screen.getByText(/0,01875 Гкал\/м²/)).toBeInTheDocument();
    expect(screen.getByText('Контакты и документы')).toBeInTheDocument();
  });

  it('сохраняет норматив теплопотребления дома', async () => {
    const mock = installFetch(detailRoutes);
    renderPage(<AbonentDetailsTab abonent={abonent} details={details} />);
    await screen.findByText('Норматив потребления');

    const panel = within(section('Норматив потребления'));
    const user = userEvent.setup();
    await user.clear(panel.getByLabelText(/Норма теплопотребления/));
    await user.type(panel.getByLabelText(/Норма теплопотребления/), '0,02');
    await user.type(panel.getByLabelText('Причина'), 'постановление');
    await user.click(panel.getByRole('button', { name: 'Сохранить норматив' }));

    expect(await screen.findByText(/Норматив сохранён/)).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents/a1/heat-norm');
    expect(call?.body).toMatchObject({ norm: 0.02, reason: 'постановление' });
  });

  it('добавляет проживающего', async () => {
    const mock = installFetch(detailRoutes);
    renderPage(<AbonentDetailsTab abonent={abonent} details={details} />);
    await screen.findByText('Проживающие: 1');
    const panel = within(section('Проживающие: 1'));
    const user = userEvent.setup();
    await user.type(panel.getByLabelText('ФИО'), 'Петров Пётр');
    await user.click(panel.getByRole('button', { name: 'Добавить проживающего' }));

    expect(await screen.findByText('Проживающий добавлен.')).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents/a1/residents');
    expect(call?.body).toMatchObject({ name: 'Петров Пётр' });
  });
});

describe('Заметка по абоненту', () => {
  it('добавляет заметку', async () => {
    const mock = installFetch({ 'POST /abonents/a1/note': { status: 204 } });
    renderPage(<AbonentNote abonent={abonent} note={null} />);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Добавить заметку' }));
    await user.type(screen.getByLabelText('Заметка'), 'Платит частями');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents/a1/note');
    expect(call?.body).toMatchObject({ note: 'Платит частями' });
  });
});
