import type { Abonent, Kopecks, ServiceLine } from './types';

// Здесь только расчёт для показа на экране. Окончательные суммы считает сервер.

export function formatMoney(value: Kopecks): string {
  return (Math.abs(value) / 100).toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/** Баланс со знаком: минус означает долг. */
export function formatBalance(value: Kopecks): string {
  if (value === 0) return '0,00';
  return `${value < 0 ? '−' : '+'}${formatMoney(value)}`;
}

/** Разбирает сумму, введённую пользователем: «1 842,10» превращает в 184210. */
export function parseMoney(input: string): Kopecks | null {
  const cleaned = input.replace(/[\s ]/g, '').replace(',', '.');
  if (cleaned === '') return null;
  const value = Number(cleaned);
  if (!Number.isFinite(value)) return null;
  return Math.round(value * 100);
}

export function parseReading(input: string): number | null {
  const cleaned = input.trim().replace(',', '.');
  if (cleaned === '') return null;
  const value = Number(cleaned);
  return Number.isFinite(value) ? value : null;
}

export function balanceOf(abonent: Pick<Abonent, 'credit' | 'services'>): Kopecks {
  return abonent.credit - abonent.services.reduce((sum, line) => sum + line.debt, 0);
}

export function balanceNote(balance: Kopecks, penalty: Kopecks = 0): string {
  if (balance > 0) return 'переплата';
  if (balance === 0) return 'долга нет';
  return penalty > 0 ? `долг, в том числе пени ${formatMoney(penalty)} ₽` : 'долг';
}

/** Начисление по новому показанию. Показание меньше прошлого не начисляется. */
export function chargeForReading(line: ServiceLine, reading: number | null): Kopecks {
  if (!line.meter || reading === null || reading < line.meter.lastReading) return 0;
  return Math.round((reading - line.meter.lastReading) * line.meter.tariff);
}

export type ReadingCheck = 'empty' | 'ok' | 'lower' | 'high';

export function checkReading(previous: number, next: number | null, average: number): ReadingCheck {
  if (next === null) return 'empty';
  const usage = next - previous;
  if (usage < 0) return 'lower';
  if (usage > average * 3) return 'high';
  return 'ok';
}
