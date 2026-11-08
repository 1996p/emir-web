import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { RoleProvider } from '@/layout/role';
import { PrintDocumentsPage } from './PrintDocumentsPage';
import { installFetch } from '@/test/utils';

const receipt = {
  id: 5, kind: 'receipt', status: 'active',
  data: {
    title: 'Квитанция', number: '0001', revision: 0, date: '30.11.2026', revisionDate: '', periodTitle: 'Ноябрь 2026', contract: '40010001',
    seller: { name: 'ООО «Тепло»', inn: '5400000000', kpp: null, address: 'рп. Чистоозерное', bankName: 'Демо-банк', bik: '045000000', corrAccount: '30101', account: '40702', director: 'Петров', chiefAccountant: 'Сидорова' },
    buyer: { name: 'Иванова Анна', inn: null, kpp: null, address: 'ул. Ленина 1-2' },
    vatRate: null,
    lines: [{ service: 'Теплоснабжение', unit: 'Гкал', unitCode: '233', volume: 1.5, price: 250000, amount: 375000, vat: 0, debt: 0 }],
    total: 375000, vatTotal: 0, debt: 0, credit: 0, toPay: 375000, totalWords: 'три тысячи семьсот пятьдесят рублей', toPayWords: 'три тысячи семьсот пятьдесят рублей', actNumber: null,
  },
};

describe('Печать документов', () => {
  it('рендерит квитанцию с данными плательщика и услугой', async () => {
    installFetch({ 'GET /documents/print': [receipt] });
    render(
      <MemoryRouter initialEntries={['/print/documents?ids=5']}>
        <RoleProvider><PrintDocumentsPage /></RoleProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText(/Квитанция за ноябрь 2026/)).toBeInTheDocument();
    expect(screen.getByText('Документов: 1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Печать' })).toBeEnabled();
    expect(screen.getByText(/Теплоснабжение/)).toBeInTheDocument();
  });
});
