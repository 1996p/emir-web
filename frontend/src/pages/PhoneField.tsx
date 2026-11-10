import { useState } from 'react';
import { checkPhone, maskPhoneInput } from '@/domain/phone';
import { Field, TextInput } from '@/ui';

/** Поле телефона с маской: «+7» подставляется само, номер оформляется по мере ввода как «+7 (913) 123 45 67».
 *  Ошибка показывается после выхода из поля, чтобы не мешать вводу. */
export function PhoneField({ value, onChange, readOnly }: { value: string; onChange: (value: string) => void; readOnly?: boolean }) {
  const [touched, setTouched] = useState(false);
  const { phone, error } = checkPhone(value);
  const shown = touched && error;
  return (
    <Field label="Телефон" hint={shown ? <span className="warn strong" role="alert">{error}</span> : undefined}>{(id) => (
      <TextInput
        id={id} type="tel" inputMode="tel" autoComplete="off" placeholder="+7 (913) 123 45 67" readOnly={readOnly} value={value}
        aria-invalid={shown ? true : undefined} style={shown ? { borderColor: 'var(--warn)' } : undefined}
        onChange={(event) => onChange(maskPhoneInput(event.target.value, value))}
        onBlur={() => { setTouched(true); if (phone && phone !== value) onChange(phone); }}
      />
    )}</Field>
  );
}
