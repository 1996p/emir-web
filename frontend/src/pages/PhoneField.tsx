import { useState } from 'react';
import { checkPhone } from '@/domain/phone';
import { Field, TextInput } from '@/ui';

/** Поле телефона: принимает 8…, +7…, скобки и пробелы, при выходе из поля приводит к виду «+7 913 123-45-67».
 *  Ошибка показывается после выхода из поля, чтобы не мешать вводу. */
export function PhoneField({ value, onChange, readOnly }: { value: string; onChange: (value: string) => void; readOnly?: boolean }) {
  const [touched, setTouched] = useState(false);
  const { phone, error } = checkPhone(value);
  const shown = touched && error;
  return (
    <Field label="Телефон" hint={shown ? <span className="warn strong" role="alert">{error}</span> : undefined}>{(id) => (
      <TextInput
        id={id} type="tel" inputMode="tel" autoComplete="off" placeholder="+7 913 123-45-67" readOnly={readOnly} value={value}
        aria-invalid={shown ? true : undefined} style={shown ? { borderColor: 'var(--warn)' } : undefined}
        onChange={(event) => onChange(event.target.value)}
        onBlur={() => { setTouched(true); if (phone && phone !== value) onChange(phone); }}
      />
    )}</Field>
  );
}
