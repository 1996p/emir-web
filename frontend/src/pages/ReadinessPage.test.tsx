import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { ReadinessPage } from './ReadinessPage';
import { installFetch, renderPage } from '@/test/utils';

const blocked = {
  period: '2026-11', reopened: false,
  readyLines: 1200, readyAbonents: 1150, wouldCharge: 1000000,
  blockedLines: 12, blockedAbonents: 10,
  problems: [
    { problem: 'Нет показаний за месяц', lines: 7, abonents: 6, examples: ['Иванов И.', 'Петров П.'] },
    { problem: 'Нет данных: норма теплопотребления дома', lines: 5, abonents: 4, examples: ['Школа № 1'] },
  ],
  houseMetersTotal: 16, houseMetersWithReading: 9,
};

describe('Экран готовности месяца', () => {
  it('показывает сводку, карточки причин и ссылки «исправить»', async () => {
    installFetch({ 'GET /period/readiness': blocked });
    renderPage(<ReadinessPage />, '/period/readiness');

    expect(await screen.findByText(/Готовность месяца, 2026-11/)).toBeInTheDocument();
    // сводка: что посчитается и сколько заблокировано
    expect(screen.getByText(/Не будут начислены/)).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();
    // прогресс по ОДПУ
    expect(screen.getByText(/9 из 16/)).toBeInTheDocument();
    // карточки причин
    expect(screen.getByText('Нет показаний за месяц')).toBeInTheDocument();
    expect(screen.getByText('Нет данных: норма теплопотребления дома')).toBeInTheDocument();
    // ссылка «исправить» для показаний ведёт в раздел показаний
    const fix = screen.getByRole('link', { name: 'Ввести показания' });
    expect(fix).toHaveAttribute('href', '/readings');
    // норму теплопотребления правим в справочнике норм
    expect(screen.getByRole('link', { name: 'Задать норму' })).toHaveAttribute('href', expect.stringContaining('/directories?name='));
  });

  it('при отсутствии проблем сообщает, что всё готово', async () => {
    installFetch({
      'GET /period/readiness': { ...blocked, blockedLines: 0, blockedAbonents: 0, problems: [] },
    });
    renderPage(<ReadinessPage />, '/period/readiness');

    expect(await screen.findByText(/Всё готово к начислению/)).toBeInTheDocument();
    expect(screen.queryByText(/Что мешает начислить/)).not.toBeInTheDocument();
  });
});
