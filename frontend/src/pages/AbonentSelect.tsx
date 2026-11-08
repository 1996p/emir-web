import { useId, useMemo, useRef, useState } from 'react';
import type { Abonent } from '@/domain/types';

const LIMIT = 50;

export const abonentAddress = (abonent: Abonent) => `${abonent.place ? `${abonent.place}, ` : ''}${abonent.street}${abonent.flat ? `-${abonent.flat}` : ''}`;
const label = (abonent: Abonent) => `${abonent.name}, ЛС ${abonent.account}`;

interface Props {
  label: string;
  abonents: Abonent[];
  value: Abonent | null;
  onChange: (abonent: Abonent) => void;
}

/** Выпадающий список абонентов с поиском по ФИО, ЛС и адресу. Список открывается по клику, ввод сужает его. */
export function AbonentSelect({ label: title, abonents, value, onChange }: Props) {
  const id = useId();
  const listId = `${id}-list`;
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const input = useRef<HTMLInputElement>(null);

  const matches = useMemo(() => {
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
    return abonents.filter((item) => {
      const text = `${item.name} ${item.shortName} ${item.account} ${abonentAddress(item)}`.toLowerCase();
      return words.every((word) => text.includes(word));
    });
  }, [abonents, query]);
  const shown = matches.slice(0, LIMIT);

  const choose = (abonent: Abonent) => { onChange(abonent); setOpen(false); setQuery(''); input.current?.blur(); };
  const onKey = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') { event.preventDefault(); setOpen(true); setActive((index) => Math.min(index + 1, shown.length - 1)); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive((index) => Math.max(index - 1, 0)); }
    else if (event.key === 'Enter' && open && shown[active]) { event.preventDefault(); choose(shown[active]); }
    else if (event.key === 'Escape') { setOpen(false); setQuery(''); }
  };

  return (
    <div style={{ position: 'relative' }}>
      <label className="field-label" htmlFor={id}>{title}</label>
      <input
        ref={input} id={id} className="input select" role="combobox" aria-expanded={open} aria-controls={listId} aria-autocomplete="list" autoComplete="off"
        placeholder={value ? label(value) : 'Выберите абонента: ФИО, ЛС или адрес'}
        value={open ? query : value ? label(value) : ''}
        onFocus={() => { setOpen(true); setActive(0); }}
        onClick={() => setOpen(true)}
        onBlur={() => { setOpen(false); setQuery(''); }}
        onChange={(event) => { setQuery(event.target.value); setOpen(true); setActive(0); }}
        onKeyDown={onKey}
      />
      {open && (
        <div id={listId} role="listbox" aria-label={title} style={{ position: 'absolute', top: '100%', left: 0, right: 0, zIndex: 5, maxHeight: 320, overflowY: 'auto', background: 'var(--surface)', border: '1px solid var(--field)', borderRadius: 6, boxShadow: '0 6px 18px rgba(0,0,0,.12)' }}>
          {shown.map((item, index) => (
            <div
              key={item.id} role="option" aria-selected={value?.id === item.id}
              // mousedown, а не click: иначе поле теряет фокус и список закрывается раньше выбора
              onMouseDown={(event) => { event.preventDefault(); choose(item); }}
              onMouseEnter={() => setActive(index)}
              style={{ padding: '6px 12px', minHeight: 40, cursor: 'pointer', borderBottom: '1px solid var(--line-soft)', background: index === active ? 'var(--tint-strong)' : undefined }}
            >
              <div className="strong">{item.name}, ЛС {item.account}</div>
              <div className="small muted">{abonentAddress(item)}</div>
            </div>
          ))}
          {matches.length === 0 && <div className="pad muted">Никого не нашли.</div>}
          {matches.length > LIMIT && <div className="small muted" style={{ padding: '6px 12px' }}>Показаны первые {LIMIT} из {matches.length}. Уточните поиск.</div>}
        </div>
      )}
    </div>
  );
}
