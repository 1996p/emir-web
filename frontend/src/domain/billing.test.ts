import { describe, expect, it } from 'vitest';
import { balanceOf, chargeForReading, checkReading, formatBalance, parseMoney } from './billing';
import type { Abonent, ServiceLine } from './types';

const heat: ServiceLine = { id: 'heat', name: 'Тепло', state: 'Работает', price: null, tariffName: '', chargeStatus: '', debt: 184210 };
const water: ServiceLine = {
  id: 'water', name: 'Вода', state: 'Работает', price: null, tariffName: '', chargeStatus: '', debt: 20790,
  meter: { number: 'С-1', lastReading: 412, tariff: 5200, verifiedUntil: '' },
};
const penalty: ServiceLine = { id: 'penalty', name: 'Пени', state: 'Работает', price: null, tariffName: '', chargeStatus: '', debt: 8450 };
const abonent: Pick<Abonent, 'credit' | 'services'> = { credit: 0, services: [heat, water, penalty] };

describe('баланс', () => {
  it('равен сумме долгов по услугам', () => {
    expect(balanceOf(abonent)).toBe(-213450);
    expect(formatBalance(balanceOf(abonent)).replace(/ /g, ' ')).toBe('−2 134,50');
  });
});

describe('сумма из ввода', () => {
  it('разбирает запятую и пробелы', () => {
    expect(parseMoney('1 842,10')).toBe(184210);
    expect(parseMoney('')).toBeNull();
    expect(parseMoney('abc')).toBeNull();
  });
});

describe('начисление по показанию', () => {
  it('считает расход и игнорирует показание меньше прошлого', () => {
    expect(chargeForReading(water, 400)).toBe(0);
    expect(chargeForReading(water, 416)).toBe(20800);
  });
});

describe('проверка показаний', () => {
  it('находит ошибки', () => {
    expect(checkReading(100, null, 5)).toBe('empty');
    expect(checkReading(100, 104, 5)).toBe('ok');
    expect(checkReading(100, 99, 5)).toBe('lower');
    expect(checkReading(100, 130, 5)).toBe('high');
  });
});
