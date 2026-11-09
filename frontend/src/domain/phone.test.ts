import { describe, it, expect } from 'vitest';
import { checkPhone, maskPhoneInput } from './phone';

describe('Телефон', () => {
  it.each([
    ['89131234567', '+7 (913) 123 45 67'],
    ['+7 (913) 123-45-67', '+7 (913) 123 45 67'],
    ['913 123 45 67', '+7 (913) 123 45 67'],
    ['8-383-512-34-56', '+7 (383) 512 34 56'],
  ])('%s → %s', (input, expected) => expect(checkPhone(input)).toEqual({ phone: expected, error: null }));

  it('пустое поле — не ошибка', () => expect(checkPhone('  ')).toEqual({ phone: null, error: null }));

  it.each(['9999999999', '12345', '+1 913 123 45 67', '1131234567', '913 123 45 67 доб', '891312345678'])('%s — ошибка', (input) => {
    expect(checkPhone(input).error).not.toBeNull();
  });
});

describe('Маска телефона при вводе', () => {
  const type = (keys: string) => [...keys].reduce((value, key) => maskPhoneInput(value + key, value), '');
  it('«+7» подставляется само, цифры группируются по мере ввода', () => {
    expect(type('9')).toBe('+7 (9');
    expect(type('913')).toBe('+7 (913)');
    expect(type('9131')).toBe('+7 (913) 1');
    expect(type('9131234')).toBe('+7 (913) 123 4');
    expect(type('9131234567')).toBe('+7 (913) 123 45 67');
    expect(type('91312345678')).toBe('+7 (913) 123 45 67');
  });
  it('начальная 8 или 7 — код страны', () => {
    expect(type('8')).toBe('+7 (');
    expect(type('89131234567')).toBe('+7 (913) 123 45 67');
  });
  it('вставка номера в любом виде', () => {
    expect(maskPhoneInput('8 (913) 123-45-67', '')).toBe('+7 (913) 123 45 67');
    expect(maskPhoneInput('9131234567', '')).toBe('+7 (913) 123 45 67');
  });
  it('стирание пробела или скобки стирает и цифру перед ним', () => {
    expect(maskPhoneInput('+7 (913) 1', '+7 (913) 1')).toBe('+7 (913) 1');
    expect(maskPhoneInput('+7 (913)', '+7 (913) ')).toBe('+7 (91');
    expect(maskPhoneInput('+7 (', '+7 (9')).toBe('');
  });
});
