import { useEffect, useRef, useState } from 'react';
import { api, errorText, useApi } from '@/api';
import type { Abonent, AddressValue } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, Field, Notice, SelectInput, TextInput } from '@/ui';

export const EMPTY_ADDRESS: AddressValue = { place: '', street: '', house: '', flat: '' };

// Сокращения типов, которые в старой системе пишутся то перед названием («ул. Мира»), то после («Мира ул.»). Так же сравнивает сервер
const TYPE_WORDS = new Set(['с', 'п', 'рп', 'р', 'пгт', 'пос', 'д', 'ст', 'х', 'г', 'ул', 'пер', 'пр', 'пр-т', 'проспект', 'кв-л', 'мкр', 'мкр-н', 'ш', 'б-р', 'пл', 'туп', 'проезд', 'тер']);

/** Название без типа, регистра и знаков: «рп. Чистоозерное» и «Чистоозерное рп.» — одно и то же. */
export function plainName(text: string): string {
  return text.toLowerCase().replace(/ё/g, 'е').split(/[\s.,]+/).filter((word) => word !== '' && !TYPE_WORDS.has(word)).join(' ');
}

/** «ул. Школьная, 12» → улица и дом. Без номера в конце вся строка — улица. */
export function splitStreet(line: string): { street: string; house: string } {
  const match = /^(.*?\S)[,\s]+(\d[^\s,]*)$/.exec(line.trim());
  return match ? { street: (match[1] ?? '').replace(/,$/, '').trim(), house: match[2] ?? '' } : { street: line.trim(), house: '' };
}

/** Адрес абонента для формы: части из справочников, а у перенесённых строкой — разобранная строка. */
export function addressOf(abonent: Abonent): AddressValue {
  const parsed = splitStreet(abonent.street);
  return { place: abonent.place, street: abonent.streetName ?? parsed.street, house: abonent.house ?? parsed.house, flat: abonent.flat ?? '' };
}

export function formatAddress(value: AddressValue): string {
  const street = [value.street, value.house].filter(Boolean).join(' ');
  return [value.place, street + (value.flat ? `-${value.flat}` : '')].filter(Boolean).join(', ');
}

export const isAddressComplete = (value: AddressValue) => Boolean(value.place && value.street && value.house);

/** Значение из списка, совпадающее по смыслу: старый адрес «рп. Чистоозерное» находит «Чистоозерное рп.». */
function matchOption(options: string[], value: string): string | null {
  if (!value || options.includes(value)) return null;
  const wanted = plainName(value);
  const found = options.filter((option) => plainName(option) === wanted);
  return found.length === 1 ? found[0] ?? null : null;
}

const NEW_REASON = 'Заведено из формы адреса абонента';

interface Props {
  value: AddressValue;
  onChange: (value: AddressValue) => void;
  /** Показывать поле квартиры */
  withFlat?: boolean;
}

/** Адрес каскадом, как в старой системе: населённый пункт → улица этого пункта → дом этой улицы, квартира руками.
 *  Улицу и дом, которых ещё нет, можно завести тут же: они попадут в справочники «Улицы» и «Дома». */
export function AddressPicker({ value, onChange, withFlat = true }: Props) {
  const role = useRole();
  const canCreate = role === 'service' || role === 'accountant' || role === 'admin';
  const { data: cities } = useApi(() => api.addresses.cities(), []);
  // Список помнит, для какого пункта (и улицы) он загружен: пока грузится новый, старый не используется
  const { data: streets } = useApi(() => (value.place ? api.addresses.streets(value.place).then((list) => ({ key: value.place, list })) : Promise.resolve({ key: '', list: [] as string[] })), [value.place]);
  const houseKey = `${value.place}|${value.street}`;
  const { data: houses } = useApi(() => (value.place && value.street ? api.addresses.houses(value.place, value.street).then((list) => ({ key: houseKey, list })) : Promise.resolve({ key: '', list: [] as string[] })), [houseKey]);
  const streetList = streets?.key === value.place ? streets.list : null;
  const houseList = houses?.key === houseKey ? houses.list : null;
  // Несколько подстановок могут случиться за один рендер: каждая достраивает последнее значение, а не то, что было при рендере
  const latest = useRef(value);
  const seen = useRef(value);
  if (seen.current !== value) { seen.current = value; latest.current = value; }
  const update = (patch: Partial<AddressValue>) => { latest.current = { ...latest.current, ...patch }; onChange(latest.current); };
  const [adding, setAdding] = useState<'street' | 'house' | null>(null);
  const [newName, setNewName] = useState('');
  const [error, setError] = useState<string | null>(null);

  // Старый адрес, записанный иначе («рп.» вместо «р.п.»), подставляется значением из справочника
  useEffect(() => {
    const place = matchOption(cities ?? [], value.place);
    if (place) update({ place });
  }, [cities, value.place]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const street = streetList ? matchOption(streetList, value.street) : null;
    if (street) update({ street });
  }, [streetList, value.street]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const house = houseList ? matchOption(houseList, value.house) : null;
    if (house) update({ house });
  }, [houseList, value.house]); // eslint-disable-line react-hooks/exhaustive-deps

  const startAdding = (what: 'street' | 'house') => { setAdding(what); setNewName(''); setError(null); };
  const add = async () => {
    const name = newName.trim().replace(/\s+/g, ' ');
    setError(null);
    try {
      if (adding === 'street') {
        await api.addresses.addStreet(value.place, name, NEW_REASON);
        update({ street: name, house: '' });
      } else {
        await api.addresses.addHouse(value.place, value.street, name, NEW_REASON);
        update({ house: name });
      }
      setAdding(null);
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  const outside = (list: string[] | null | undefined, current: string) => (current && list && !list.includes(current) ? [current] : []);
  const addForm = (label: string, placeholder: string) => (
    <div className="stack" style={{ gap: 6, padding: 8, border: '1px solid var(--line)', borderRadius: 6 }}>
      <Field label={label}>{(id) => <TextInput id={id} autoFocus placeholder={placeholder} value={newName} onChange={(event) => setNewName(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && newName.trim()) { event.preventDefault(); void add(); } }} />}</Field>
      {error && <Notice tone="warn">{error}</Notice>}
      <div className="row">
        <Button variant="primary" size="sm" disabled={!newName.trim()} onClick={() => void add()}>Добавить</Button>
        <Button size="sm" onClick={() => setAdding(null)}>Отмена</Button>
      </div>
    </div>
  );

  return (
    <div className="stack" style={{ gap: 8 }}>
      <Field label="Населённый пункт">{(id) => (
        <SelectInput id={id} value={value.place} onChange={(event) => { setAdding(null); update({ place: event.target.value, street: '', house: '' }); }}>
          <option value="">— выберите —</option>
          {outside(cities, value.place).map((item) => <option key={item} value={item}>{item} (нет в справочнике)</option>)}
          {(cities ?? []).map((item) => <option key={item} value={item}>{item}</option>)}
        </SelectInput>
      )}</Field>
      {cities && cities.length === 0 && <Notice tone="warn">Справочник «Города» пуст. Заведите населённый пункт в разделе «Справочники».</Notice>}

      <Field label="Улица">{(id) => (
        <div className="row" style={{ flexWrap: 'nowrap' }}>
          <SelectInput id={id} value={value.street} disabled={!value.place} onChange={(event) => { setAdding(null); update({ street: event.target.value, house: '' }); }}>
            <option value="">{value.place ? '— выберите —' : '— сначала населённый пункт —'}</option>
            {outside(streetList, value.street).map((item) => <option key={item} value={item}>{item} (нет в справочнике)</option>)}
            {(streetList ?? []).map((item) => <option key={item} value={item}>{item}</option>)}
          </SelectInput>
          {canCreate && value.place && <Button size="auto" style={{ flex: '0 0 auto' }} active={adding === 'street'} onClick={() => startAdding('street')}>Новая</Button>}
        </div>
      )}</Field>
      {adding === 'street' && addForm(`Новая улица в «${value.place}»`, 'Например: ул. Полевая')}

      <Field label="Дом">{(id) => (
        <div className="row" style={{ flexWrap: 'nowrap' }}>
          <SelectInput id={id} value={value.house} disabled={!value.street} onChange={(event) => { setAdding(null); update({ house: event.target.value }); }}>
            <option value="">{value.street ? '— выберите —' : '— сначала улица —'}</option>
            {outside(houseList, value.house).map((item) => <option key={item} value={item}>{item} (нет в справочнике)</option>)}
            {(houseList ?? []).map((item) => <option key={item} value={item}>{item}</option>)}
          </SelectInput>
          {canCreate && value.street && <Button size="auto" style={{ flex: '0 0 auto' }} active={adding === 'house'} onClick={() => startAdding('house')}>Новый</Button>}
        </div>
      )}</Field>
      {adding === 'house' && addForm(`Новый дом на «${value.street}»`, 'Номер: 12, 12а, 16/1')}

      {withFlat && <Field label="Квартира" hint="Пусто для частного дома">{(id) => <TextInput id={id} maxLength={20} value={value.flat} onChange={(event) => update({ flat: event.target.value })} />}</Field>}
      {/* Как строка «выбрано» в старой системе: итоговый адрес перед сохранением */}
      {value.place && <div className="small" aria-live="polite"><span className="muted">Выбрано: </span>{formatAddress({ ...value, flat: withFlat ? value.flat.trim() : '' })}</div>}
    </div>
  );
}
