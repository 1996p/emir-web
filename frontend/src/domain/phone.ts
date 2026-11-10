/** Телефон: те же правила, что на сервере (Registration.Phone). */
export const PHONE_HINT = 'Телефон: 10 цифр с кодом города или оператора, например +7 (913) 123 45 67';

/** Номер в виде «+7 (913) 123 45 67». Неполный номер оформляется по мере ввода: «+7 (91», «+7 (913) 12». */
export function formatPhone(digits: string): string {
  const d = digits.slice(0, 10);
  if (d === '') return '';
  let text = `+7 (${d.slice(0, 3)}`;
  if (d.length >= 3) text += ')';
  if (d.length > 3) text += ` ${d.slice(3, 6)}`;
  if (d.length > 6) text += ` ${d.slice(6, 8)}`;
  if (d.length > 8) text += ` ${d.slice(8, 10)}`;
  return text;
}

/** Цифры номера без кода страны: «+7» в начале, а также 8 или 7 первой цифрой (кроме ровно 10 цифр) — код страны. */
export function phoneDigits(input: string): string {
  const text = input.trim();
  const digits = text.replace(/\D/g, '');
  if (text.startsWith('+7')) return digits.slice(1, 11);
  if (!text.startsWith('+') && (digits[0] === '8' || digits[0] === '7') && digits.length !== 10) return digits.slice(1, 11);
  return digits.slice(0, 10);
}

/** Маска при вводе: что бы ни ввели или вставили (89131234567, 913 123 45 67, +7 (913)…), поле показывает «+7 (913) 123 45 67».
 *  Если стёрли скобку или пробел, стирается и цифра перед ним — иначе маска вернула бы символ обратно. */
export function maskPhoneInput(next: string, previous: string): string {
  let digits = phoneDigits(next);
  if (next.length < previous.length && digits === phoneDigits(previous)) digits = digits.slice(0, -1);
  // Набрали только «8» или «7»: это код страны, показываем начало маски
  if (digits === '' && /\d/.test(next) && next.length > previous.length) return '+7 (';
  return formatPhone(digits);
}

/** Номер в едином виде «+7 (913) 123 45 67», или текст ошибки. Пустое поле — телефона нет, это не ошибка. */
export function checkPhone(input: string): { phone: string | null; error: string | null } {
  const text = input.trim();
  if (text === '' || text === '+7 (' || text === '+7') return { phone: null, error: null };
  if (/[^\d\s()+\-.]/.test(text)) return { phone: null, error: PHONE_HINT };
  let digits = text.replace(/\D/g, '');
  if (digits.length === 11 && (digits[0] === '7' || digits[0] === '8')) digits = digits.slice(1);
  else if (text.startsWith('+')) return { phone: null, error: PHONE_HINT };
  if (digits.length !== 10 || !'3489'.includes(digits[0] ?? '')) return { phone: null, error: PHONE_HINT };
  if (new Set(digits).size === 1) return { phone: null, error: 'Телефон похож на ненастоящий: все цифры одинаковые' };
  return { phone: formatPhone(digits), error: null };
}
