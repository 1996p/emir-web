import { describe, it, expect } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { installFetch, renderPage, type Routes } from '@/test/utils';
import { AbonentsPage } from './AbonentsPage';
import { CashPage } from './CashPage';
import { ReportsPage } from './ReportsPage';
import { ChecksPage } from './ChecksPage';
import { ExchangePage } from './ExchangePage';
import { CorrectionsPage } from './CorrectionsPage';
import { ReadingsPage } from './ReadingsPage';
import { TodayPage } from './TodayPage';
import { AdminPage } from './AdminPage';
import { DocumentsPage } from './DocumentsPage';

const current = { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null };
const tariffs = { openPeriod: '2026-11', reopened: false, tariffs: [] };
const profile = {
  name: 'ООО «Тепло»', inn: '5400000000', kpp: null, address: 'рп. Чистоозерное', bankName: null, bik: null,
  corrAccount: null, account: null, director: null, chiefAccountant: null, vatRate: null, heatSeasonStartMonth: 10, heatSeasonEndMonth: 4,
};

type Locate = () => Promise<unknown>;

const cases: Array<{ name: string; element: React.ReactElement; path: string; routes: Routes; locate?: Locate }> = [
  { name: 'Абоненты', element: <AbonentsPage />, path: '/abonents', routes: { 'GET /abonents': [], 'GET /period/current': current, 'GET /tariffs': tariffs }, locate: () => screen.findByLabelText('Поиск') },
  { name: 'Касса', element: <CashPage />, path: '/cash', routes: { 'GET /abonents': [], 'GET /cash/shift': { entries: [], count: 0, total: 0 } }, locate: () => screen.findByPlaceholderText(/Найти абонента/) },
  { name: 'Отчёты', element: <ReportsPage />, path: '/reports', routes: { 'GET /reports/catalog': [], 'GET /reports/run': { available: false, message: null, columns: [], rows: [], totals: [] } }, locate: () => screen.findByPlaceholderText('Найти отчёт') },
  { name: 'Сверки', element: <ChecksPage />, path: '/checks', routes: { 'GET /checks': { period: '2026-11', title: 'Ноябрь 2026', months: [], groups: [] } }, locate: () => screen.findByText('Сверки') },
  { name: 'Обмен', element: <ExchangePage />, path: '/exchange', routes: { 'GET /exchange/batches': [] }, locate: () => screen.findByText(/CSV или TXT/) },
  { name: 'Корректировки', element: <CorrectionsPage />, path: '/corrections', routes: { 'GET /abonents': [], 'GET /corrections/types': [], 'GET /period/current': current } },
  { name: 'Показания', element: <ReadingsPage />, path: '/readings', routes: { 'GET /readings/houses': [], 'GET /period/current': current }, locate: () => screen.findByPlaceholderText(/Найти дом/) },
  { name: 'Сегодня', element: <TodayPage />, path: '/', routes: { 'GET /today': { tasks: [], actions: [], messages: [] }, 'GET /period/current': current, 'GET /period/steps': [] }, locate: () => screen.findByText('Сегодня') },
  { name: 'Админ', element: <AdminPage />, path: '/admin', routes: { 'GET /admin/sections': [{ id: 's1', group: 'Доступ', title: 'Роли и доступы', description: '', columns: ['Роль'], rows: [] }] }, locate: () => screen.findAllByText('Роли и доступы') },
  { name: 'Документы', element: <DocumentsPage />, path: '/documents', routes: { 'GET /documents': { period: '2026-11', title: 'Ноябрь 2026', months: [], canGenerate: false, cannotGenerate: null, documents: [] }, 'GET /documents/counters': [], 'GET /documents/profile': profile }, locate: () => screen.findAllByText(/Документы|Квитанции/) },
];

describe('Экраны приложения монтируются с замоканным API', () => {
  for (const item of cases) {
    it(`${item.name}`, async () => {
      installFetch(item.routes);
      renderPage(item.element, item.path);
      // сессия загрузилась — «ворота» пропустили экран
      await waitFor(() => expect(screen.queryByTestId('session-loading')).toBeNull());
      if (item.locate) await item.locate();
      // все запросы экрана замоканы — нет обращений к незамоканным ручкам
      expect(document.body.textContent ?? '').not.toContain('нет мока');
    });
  }
});
