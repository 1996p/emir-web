import { describe, it, expect } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HeatNormsDirectory } from './HeatNormsDirectory';
import { installFetch, renderPage } from '@/test/utils';

const list = {
  categories: [
    { category: 'Кирпичный 5эт', abonents: 40, withoutNorm: 40, norm: null, distinct: [] },
  ],
  withoutCategory: 105,
};

describe('Справочник норм теплопотребления', () => {
  it('показывает категории и предупреждение про абонентов без категории', async () => {
    installFetch({ 'GET /heat-norms': list });
    renderPage(<HeatNormsDirectory filter="" />);

    expect((await screen.findAllByText('Кирпичный 5эт')).length).toBeGreaterThan(0);
    expect(screen.getByText(/У 105 абонентов не указана категория/)).toBeInTheDocument();
  });

  it('применяет норму ко всей категории и показывает результат', async () => {
    const mock = installFetch({ 'GET /heat-norms': list });
    mock.set('POST /heat-norms/apply', (body: unknown) => {
      // после применения список отдаёт уже проставленную норму
      mock.set('GET /heat-norms', { ...list, categories: [{ ...list.categories[0], withoutNorm: 0, norm: (body as { norm: number }).norm, distinct: [(body as { norm: number }).norm] }] });
      return { category: 'Кирпичный 5эт', norm: (body as { norm: number }).norm, total: 40, changed: 40 };
    });
    renderPage(<HeatNormsDirectory filter="" />);

    await screen.findAllByText('Кирпичный 5эт');
    const user = userEvent.setup();
    await user.type(screen.getByLabelText(/Норма теплопотребления/), '0,0188');
    await user.type(screen.getByLabelText('Основание'), 'приказ 12');
    await user.click(screen.getByRole('button', { name: /Применить ко всем в категории/ }));

    expect(await screen.findByText(/Норма проставлена 40 из 40 абонентов/)).toBeInTheDocument();
    await waitFor(() => expect(mock.calls.some((call) => call.method === 'POST' && call.path === '/heat-norms/apply' && (call.body as { norm: number }).norm === 0.0188)).toBe(true));
  });
});
