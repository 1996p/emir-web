import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DocumentsPage } from './DocumentsPage';
import { CancelChargesPage } from './CancelChargesPage';
import { installFetch, renderPage } from '@/test/utils';

const profile = {
  name: 'ООО «Тепло»', inn: '5400000000', kpp: null, address: 'рп. Чистоозерное', bankName: null, bik: null,
  corrAccount: null, account: null, director: null, chiefAccountant: null, vatRate: null, heatSeasonStartMonth: 10, heatSeasonEndMonth: 4,
};
const documentsView = { period: '2026-11', title: 'Ноябрь 2026', months: [{ period: '2026-11', title: 'Ноябрь 2026' }], canGenerate: false, cannotGenerate: 'Месяц не закрыт', documents: [] };

describe('Документы — реквизиты и отопительный сезон', () => {
  it('сохраняет реквизиты с выбранным отопительным сезоном', async () => {
    const mock = installFetch({
      'GET /documents': documentsView,
      'GET /documents/counters': [],
      'GET /documents/profile': profile,
      'POST /documents/profile': { status: 204 },
    });
    renderPage(<DocumentsPage />, '/documents');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Реквизиты и номера' }));
    const panel = within(await screen.findByRole('complementary', { name: 'Реквизиты и нумерация' }));
    expect(panel.getByText('Отопительный сезон')).toBeInTheDocument();
    // сезон с сентября
    await user.selectOptions(panel.getByLabelText('С месяца'), '9');
    await user.click(panel.getByRole('button', { name: 'Сохранить реквизиты' }));

    expect(await screen.findByText(/Реквизиты сохранены/)).toBeInTheDocument();
    const call = mock.calls.find((c) => c.method === 'POST' && c.path === '/documents/profile');
    expect(call?.body).toMatchObject({ heatSeasonStartMonth: 9, heatSeasonEndMonth: 4 });
  });
});

describe('Отмена начислений', () => {
  it('показывает предпросмотр с услугами месяца', async () => {
    installFetch({
      'GET /period/cancel/preview': {
        period: '2026-11', title: 'Ноябрь 2026', total: 500000,
        lines: [], services: [{ id: 'heat', name: 'Теплоснабжение', total: 500000 }], places: ['рп. Чистоозерное'], chargesDone: true, penaltyDone: false,
      },
    });
    renderPage(<CancelChargesPage />, '/period/cancel');

    expect(await screen.findByRole('heading', { name: /Отмена начислений/ })).toBeInTheDocument();
    expect(screen.getByText('Теплоснабжение')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'К месяцу' })).toHaveAttribute('href', '/period');
  });
});
