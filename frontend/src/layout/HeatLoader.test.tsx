import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { HeatLoader } from './HeatLoader';
import { useApi } from '@/api';

/** Держит один незавершённый запрос, чтобы счётчик pending был > 0. */
function Pending() {
  useApi(() => new Promise<never>(() => {}), []);
  return null;
}

describe('HeatLoader', () => {
  it('появляется по таймеру, пока есть незавершённые запросы', async () => {
    render(<><Pending /><HeatLoader /></>);
    expect(await screen.findByRole('status')).toBeInTheDocument();
  });

  it('не показывается, когда запросов нет', () => {
    render(<HeatLoader />);
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
