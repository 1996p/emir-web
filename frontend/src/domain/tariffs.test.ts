import { describe, expect, it } from 'vitest';
import { formatPeriod, nextPeriod, parsePrice } from './tariffs';

describe('tariffs', () => {
  it('parsePrice принимает запятую и отклоняет лишнее', () => {
    expect(parsePrice('35,4321')).toBe(35.4321);
    expect(parsePrice(' 1 200.5 ')).toBe(1200.5);
    expect(parsePrice('0')).toBeNull();
    expect(parsePrice('-5')).toBeNull();
    expect(parsePrice('1,23456')).toBeNull();
    expect(parsePrice('цена')).toBeNull();
  });

  it('nextPeriod переходит через год', () => {
    expect(nextPeriod('2026-11')).toBe('2026-12');
    expect(nextPeriod('2026-12')).toBe('2027-01');
  });

  it('formatPeriod называет месяц и начало учёта', () => {
    expect(formatPeriod('2026-12')).toBe('декабрь 2026');
    expect(formatPeriod('2000-01')).toBe('С начала учёта');
  });
});
