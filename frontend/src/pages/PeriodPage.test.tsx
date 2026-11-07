import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PeriodPage } from './PeriodPage';
import { installFetch, renderPage } from '@/test/utils';

const base = {
  'GET /period/settings': [],
  'GET /period/closed': [],
  'GET /period/current': { period: '2026-11', title: 'Ноябрь 2026', status: 'open', nextTitle: null, reopenReason: null },
  'GET /period/steps': [
    { id: 'charges', name: 'Начисления', status: 'идёт', state: 'current', description: 'Расчёт месяца', stats: [], issues: [], action: 'Начислить', confirmTitle: null, confirmText: null, confirmAction: null },
  ],
};

describe('Страница месяца', () => {
  it('ведёт на экран готовности месяца', async () => {
    installFetch(base);
    renderPage(<PeriodPage />, '/period');
    expect(await screen.findByRole('link', { name: 'Готовность месяца' })).toHaveAttribute('href', '/period/readiness');
  });

  it('в пробном расчёте разводит «уже начислено» и «будет начислено»', async () => {
    installFetch({
      ...base,
      'GET /period/charges/preview': {
        abonents: 1320, lines: 1368, total: 10000000, already: 313693,
        services: [{ service: 'Теплоснабжение', abonents: 1298, previous: 9304732, already: 313693, current: 7634943 }],
        issues: [],
      },
    });
    renderPage(<PeriodPage />, '/period');

    const user = userEvent.setup();
    await user.click(await screen.findByRole('tab', { name: /Пробный расчёт/ }));

    // сводка показывает «уже проведено» и прибавку к долгу
    expect(await screen.findByText(/Уже проведено/)).toBeInTheDocument();
    expect(screen.getByText(/добавит к долгу/)).toBeInTheDocument();
    // в таблице появляется колонка «Уже начислено»
    expect(screen.getByText('Уже начислено, ₽')).toBeInTheDocument();
  });
});
