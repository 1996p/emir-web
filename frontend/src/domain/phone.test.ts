import { describe, it, expect } from 'vitest';
import { checkPhone } from './phone';

describe('Телефон', () => {
  it.each([
    ['89131234567', '+7 913 123-45-67'],
    ['+7 (913) 123-45-67', '+7 913 123-45-67'],
    ['913 123 45 67', '+7 913 123-45-67'],
    ['8-383-512-34-56', '+7 383 512-34-56'],
  ])('%s → %s', (input, expected) => expect(checkPhone(input)).toEqual({ phone: expected, error: null }));

  it('пустое поле — не ошибка', () => expect(checkPhone('  ')).toEqual({ phone: null, error: null }));

  it.each(['9999999999', '12345', '+1 913 123 45 67', '1131234567', '913 123 45 67 доб', '891312345678'])('%s — ошибка', (input) => {
    expect(checkPhone(input).error).not.toBeNull();
  });
});
