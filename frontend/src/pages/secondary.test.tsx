import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { RoleProvider } from '@/layout/role';
import { installFetch, renderPage } from '@/test/utils';
import { TariffsDirectory } from './TariffsDirectory';
import { BeneficiariesDirectory, CategoriesDirectory } from './BenefitsDirectory';
import { UsersPanel } from './UsersPanel';
import { ReclosePanel } from './ReclosePanel';
import { ReadingImportPage } from './ReadingImportPage';
import { PrintDocumentsPage } from './PrintDocumentsPage';
import { PrintStatementPage } from './PrintStatementPage';

describe('Справочник тарифов', () => {
  it('показывает тариф и его схему расчёта', async () => {
    installFetch({
      'GET /tariffs': {
        openPeriod: '2026-11', reopened: false,
        tariffs: [{ id: 't1', name: 'Отопление', scheme: 'NormHouse', currentPrice: 250000, lines: 12, prices: [], unit: 'Гкал', serviceNorm: null, tariffNorm: null, penaltyEnabled: true }],
      },
    });
    renderPage(<TariffsDirectory filter="" />);
    expect((await screen.findAllByText('Отопление')).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Площадь × норма теплопотребления дома/).length).toBeGreaterThan(0);
  });
});

describe('Справочник льготников', () => {
  it('список льготников монтируется с фильтром состояния', async () => {
    installFetch({ 'GET /benefits': [], 'GET /benefits/categories': [] });
    renderPage(<BeneficiariesDirectory filter="" />);
    expect(await screen.findByLabelText('Состояние')).toBeInTheDocument();
  });

  it('категории льгот показывают пустое состояние', async () => {
    installFetch({ 'GET /benefits/categories': [] });
    renderPage(<CategoriesDirectory filter="" />);
    expect(await screen.findByText('Категорий нет.')).toBeInTheDocument();
  });
});

describe('Панель пользователей (админ)', () => {
  it('показывает заголовок и список сотрудников', async () => {
    installFetch({
      'GET /admin/users': [{ id: 1, login: 'buh1', displayName: 'Бухгалтер', role: 'accountant', isActive: true, mustChangePassword: false, locked: false, lastLogin: null }],
    });
    renderPage(<UsersPanel />);
    expect(await screen.findByText('Сотрудники')).toBeInTheDocument();
    expect(screen.getByText('buh1')).toBeInTheDocument();
    expect(screen.getAllByText('Бухгалтер').length).toBeGreaterThan(0);
  });
});

describe('Панель перезакрытия месяца', () => {
  it('показывает предпросмотр перезакрытия', async () => {
    installFetch({
      'GET /period/reclose/preview': { period: '2026-10', title: 'Октябрь 2026', delta: 0, changes: [], issues: [], blocking: [] },
    });
    const month = { period: '2026-10', title: 'Октябрь 2026', status: 'reopened' as const, nextTitle: null, reopenReason: 'правка' };
    renderPage(<ReclosePanel month={month} onDone={() => {}} />);
    expect((await screen.findAllByText(/Перезакрытие/)).length).toBeGreaterThan(0);
  });
});

describe('Импорт показаний', () => {
  it('показывает форму загрузки файла', async () => {
    installFetch({});
    renderPage(<ReadingImportPage />, '/readings/import');
    expect(await screen.findByText('Точка с запятой')).toBeInTheDocument();
  });
});

describe('Печать', () => {
  it('страница документов на печать показывает счётчик и отключает печать без документов', async () => {
    installFetch({ 'GET /documents/print': [] });
    render(
      <MemoryRouter initialEntries={['/print/documents?ids=']}>
        <RoleProvider><PrintDocumentsPage /></RoleProvider>
      </MemoryRouter>,
    );
    expect(await screen.findByText('Документов: 0')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Печать' })).toBeDisabled();
  });

  it('справка о состоянии лицевого счёта печатает данные абонента', async () => {
    installFetch({
      'GET /abonents/a1': { id: 'a1', kind: 'person', name: 'Иванова Анна', shortName: 'Иванова А.', place: 'рп. Чистоозерное', street: 'ул. Ленина 1', flat: '2', account: '40010001', phone: null, inn: null, residents: 1, area: 50, credit: 0, services: [] },
      'GET /documents/profile': { name: 'ООО «Тепло»', inn: '5400000000', kpp: null, address: 'рп. Чистоозерное', bankName: null, bik: null, corrAccount: null, account: null, director: null, chiefAccountant: null, vatRate: null, heatSeasonStartMonth: 10, heatSeasonEndMonth: 4 },
    });
    render(
      <MemoryRouter initialEntries={['/print/abonent/a1']}>
        <RoleProvider>
          <Routes><Route path="/print/abonent/:id" element={<PrintStatementPage />} /></Routes>
        </RoleProvider>
      </MemoryRouter>,
    );
    expect(await screen.findByText('Справка о состоянии лицевого счёта')).toBeInTheDocument();
    expect(screen.getByText('Иванова Анна')).toBeInTheDocument();
    expect(screen.getByText('40010001')).toBeInTheDocument();
  });
});
