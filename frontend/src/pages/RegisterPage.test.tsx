import { describe, it, expect } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RegisterPage } from './RegisterPage';
import { installFetch, renderPage, type Routes } from '@/test/utils';

const streets: Record<string, string[]> = { 'р.п. Чистоозерное': ['ул. Школьная'], 'с. Журавка': ['ул. Новая'] };
const addresses = (houses: string[] = ['12', '14']): Routes => ({
  'GET /addresses/cities': ['р.п. Чистоозерное', 'с. Журавка'],
  'GET /addresses/streets': (_body: unknown, query: URLSearchParams) => streets[query.get('place') ?? ''] ?? [],
  'GET /addresses/houses': (_body: unknown, query: URLSearchParams) => (query.get('street') === 'ул. Школьная' ? houses : []),
});

describe('Регистрация абонента', () => {
  it('адрес выбирается каскадом: населённый пункт → улица → дом', async () => {
    const mock = installFetch({ 'GET /abonents': [], ...addresses(), 'POST /abonents': { id: 'new' } });
    renderPage(<RegisterPage />, '/abonents/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('ФИО'), 'Иванов Иван');
    expect(screen.getByLabelText('Улица')).toBeDisabled();
    await user.selectOptions(await screen.findByLabelText('Населённый пункт'), await screen.findByRole('option', { name: 'р.п. Чистоозерное' }));
    // улицы только выбранного пункта
    await user.selectOptions(screen.getByLabelText('Улица'), await screen.findByRole('option', { name: 'ул. Школьная' }));
    expect(screen.queryByRole('option', { name: 'ул. Новая' })).toBeNull();
    await user.selectOptions(screen.getByLabelText('Дом'), await screen.findByRole('option', { name: '12' }));
    await user.type(screen.getByLabelText('Квартира'), '5');
    await user.click(screen.getByRole('button', { name: /Создать абонента/ }));

    await waitFor(() => expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/abonents')).toBe(true));
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents');
    expect(call?.body).toMatchObject({ kind: 'person', name: 'Иванов Иван', place: 'р.п. Чистоозерное', street: 'ул. Школьная', house: '12', flat: '5' });
  });

  it('новый дом заводится из формы и сразу выбирается', async () => {
    let houses = ['12'];
    const mock = installFetch({
      'GET /abonents': [], ...addresses(),
      'GET /addresses/houses': () => houses,
      'POST /addresses/houses': (body: unknown) => { houses = [...houses, (body as { house: string }).house]; return { status: 204 }; },
    }, 'service');
    renderPage(<RegisterPage />, '/abonents/new');
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText('Населённый пункт'), await screen.findByRole('option', { name: 'р.п. Чистоозерное' }));
    await user.selectOptions(screen.getByLabelText('Улица'), await screen.findByRole('option', { name: 'ул. Школьная' }));
    await user.click(await screen.findByRole('button', { name: 'Новый' }));
    await user.type(screen.getByLabelText(/Новый дом на/), '16/1');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));

    await waitFor(() => expect(screen.getByLabelText('Дом')).toHaveValue('16/1'));
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/addresses/houses');
    expect(call?.body).toMatchObject({ place: 'р.п. Чистоозерное', street: 'ул. Школьная', house: '16/1' });
  });

  it('кассир не видит кнопок «Новая» и «Новый»', async () => {
    installFetch({ 'GET /abonents': [], ...addresses() }, 'cashier');
    renderPage(<RegisterPage />, '/abonents/new');
    const user = userEvent.setup();
    await user.selectOptions(await screen.findByLabelText('Населённый пункт'), await screen.findByRole('option', { name: 'р.п. Чистоозерное' }));
    await screen.findByRole('option', { name: 'ул. Школьная' });
    expect(screen.queryByRole('button', { name: 'Новая' })).toBeNull();
  });

  it('показывает ошибку сервера', async () => {
    installFetch({ 'GET /abonents': [], ...addresses(), 'POST /abonents': { status: 422, data: { detail: 'Дома «12» нет в справочнике «Дома»' } } });
    renderPage(<RegisterPage />, '/abonents/new');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('ФИО'), 'Иванов Иван');
    await user.selectOptions(await screen.findByLabelText('Населённый пункт'), await screen.findByRole('option', { name: 'р.п. Чистоозерное' }));
    await user.selectOptions(screen.getByLabelText('Улица'), await screen.findByRole('option', { name: 'ул. Школьная' }));
    await user.selectOptions(screen.getByLabelText('Дом'), await screen.findByRole('option', { name: '12' }));
    await user.click(screen.getByRole('button', { name: /Создать абонента/ }));
    expect(await screen.findByText(/нет в справочнике/)).toBeInTheDocument();
  });

  it('кнопка заблокирована, пока не заполнены имя и адрес', async () => {
    installFetch({ 'GET /abonents': [], ...addresses() });
    renderPage(<RegisterPage />, '/abonents/new');
    expect(await screen.findByRole('button', { name: /Заполните имя и адрес/ })).toBeDisabled();
  });

  it('отправляет все поля формы: контакты, паспорт, проживающего и услуги со счётчиком', async () => {
    const mock = installFetch({ 'GET /abonents': [], ...addresses(), 'POST /abonents': { id: 'new' } });
    renderPage(<RegisterPage />, '/abonents/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('ФИО'), 'Иванов Иван');
    await user.type(screen.getByLabelText('E-mail, если есть'), 'ivanov@example.ru');
    await user.type(screen.getByLabelText('Дата рождения'), '1980-05-17');
    await user.click(screen.getByText('Паспорт, если нужен для договора'));
    await user.type(screen.getByLabelText('Серия'), '5001');
    await user.type(screen.getByLabelText('Номер'), '123456');
    await user.type(screen.getByLabelText('Код подразделения'), '540-001');
    await user.click(screen.getByLabelText('Добавить в проживающие'));
    await user.selectOptions(await screen.findByLabelText('Населённый пункт'), await screen.findByRole('option', { name: 'р.п. Чистоозерное' }));
    await user.selectOptions(screen.getByLabelText('Улица'), await screen.findByRole('option', { name: 'ул. Школьная' }));
    await user.selectOptions(screen.getByLabelText('Дом'), await screen.findByRole('option', { name: '12' }));
    await user.selectOptions(screen.getByLabelText('Холодная вода'), 'meter');
    expect(screen.getByRole('button', { name: 'Укажите номер счётчика' })).toBeDisabled();
    await user.type(screen.getByLabelText('Номер счётчика'), 'ВК-1');
    await user.type(screen.getByLabelText('Начальное показание'), '15,5');
    await user.click(screen.getByRole('button', { name: 'Создать абонента' }));

    await waitFor(() => expect(mock.calls.some((c) => c.method === 'POST' && c.path === '/abonents')).toBe(true));
    expect(mock.calls.find((c) => c.method === 'POST' && c.path === '/abonents')?.body).toMatchObject({
      email: 'ivanov@example.ru', birthDate: '1980-05-17', addResident: false,
      passport: { series: '5001', number: '123456', departmentCode: '540-001' },
      services: { heat: 'norm', water: 'meter', meterNumber: 'ВК-1', meterReading: 15.5 },
    });
  });

  it('неверный телефон: подсказка после выхода из поля и кнопка заблокирована, верный приводится к одному виду', async () => {
    installFetch({ 'GET /abonents': [], ...addresses() });
    renderPage(<RegisterPage />, '/abonents/new');
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('ФИО'), 'Иванов Иван');
    await user.selectOptions(await screen.findByLabelText('Населённый пункт'), await screen.findByRole('option', { name: 'р.п. Чистоозерное' }));
    await user.selectOptions(screen.getByLabelText('Улица'), await screen.findByRole('option', { name: 'ул. Школьная' }));
    await user.selectOptions(screen.getByLabelText('Дом'), await screen.findByRole('option', { name: '12' }));

    const phone = screen.getByLabelText('Телефон');
    await user.type(phone, '9999999999');
    await user.tab();
    expect(await screen.findByRole('alert')).toHaveTextContent(/ненастоящий/);
    expect(screen.getByRole('button', { name: 'Проверьте телефон' })).toBeDisabled();

    await user.clear(phone);
    await user.type(phone, '89131234567');
    await user.tab();
    expect(phone).toHaveValue('+7 (913) 123 45 67');
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByRole('button', { name: 'Создать абонента' })).toBeEnabled();
  });
});
