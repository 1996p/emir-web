/** Телефон: те же правила, что на сервере (Registration.Phone). */
export const PHONE_HINT = 'Телефон: 10 цифр с кодом города или оператора, например +7 913 123-45-67';

/** Номер в едином виде «+7 913 123-45-67», или текст ошибки. Пустое поле — телефона нет, это не ошибка. */
export function checkPhone(input: string): { phone: string | null; error: string | null } {
  const text = input.trim();
  if (text === '') return { phone: null, error: null };
  if (/[^\d\s()+\-.]/.test(text)) return { phone: null, error: PHONE_HINT };
  let digits = text.replace(/\D/g, '');
  if (digits.length === 11 && (digits[0] === '7' || digits[0] === '8')) digits = digits.slice(1);
  else if (text.startsWith('+')) return { phone: null, error: PHONE_HINT };
  if (digits.length !== 10 || !'3489'.includes(digits[0] ?? '')) return { phone: null, error: PHONE_HINT };
  if (new Set(digits).size === 1) return { phone: null, error: 'Телефон похож на ненастоящий: все цифры одинаковые' };
  return { phone: `+7 ${digits.slice(0, 3)} ${digits.slice(3, 6)}-${digits.slice(6, 8)}-${digits.slice(8)}`, error: null };
}
