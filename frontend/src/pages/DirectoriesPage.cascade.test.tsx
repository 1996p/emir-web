import { describe, it, expect } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DirectoriesPage } from './DirectoriesPage';
import { installFetch, renderPage } from '@/test/utils';

const streets: Record<string, string[]> = { Журавка: ['Морозова', 'Центральная'], Степное: ['Школьная'] };

describe('Справочники: адресная цепочка Город → Улица', () => {
  it('улицы берутся из выбранного города, смена города сбрасывает улицу', async () => {
    const mock = installFetch({
      'GET /directories/groups': [{ name: 'Адреса', items: ['Дома'] }],
      'GET /directories/table': {
        columns: ['ID', 'Город', 'Улица', 'Дом'], rows: [['1', 'Журавка', 'Центральная', '1']], editable: true,
        references: { Город: { source: 'Города', label: 'Наименование' }, Улица: { source: 'Улицы', label: 'Улица', where: { 'Населённый пункт': 'Город' } } },
      },
      'GET /directories/options': (_body: unknown, query: URLSearchParams) => query.get('name') === 'Города'
        ? ['Журавка', 'Степное']
        : streets[(JSON.parse(query.get('where') ?? '{}') as Record<string, string>)['Населённый пункт'] ?? ''] ?? [],
    });
    renderPage(<DirectoriesPage />, '/directories?name=Дома');

    const record = await screen.findByRole('complementary', { name: 'Запись' });
    const street = await within(record).findByLabelText('Улица');
    await waitFor(() => expect(within(street).getByRole('option', { name: 'Морозова' })).toBeInTheDocument());
    expect(within(street).queryByRole('option', { name: 'Школьная' })).toBeNull();

    const user = userEvent.setup();
    await user.selectOptions(within(record).getByLabelText('Город'), 'Степное');
    expect((within(record).getByLabelText('Улица') as HTMLSelectElement).value).toBe('');
    await waitFor(() => expect(within(within(record).getByLabelText('Улица')).getByRole('option', { name: 'Школьная' })).toBeInTheDocument());
    expect(mock.calls.some((call) => call.path === '/directories/options')).toBe(true);
  });
});
