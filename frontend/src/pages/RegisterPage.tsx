import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { parseReading } from '@/domain/billing';
import type { AbonentKind, AddressValue, HeatChoice, RegisterAbonentRequest, WaterChoice } from '@/domain/types';
import { checkPhone } from '@/domain/phone';
import { PhoneField } from './PhoneField';
import { AddressPicker, EMPTY_ADDRESS, isAddressComplete, plainName } from './AddressPicker';
import { Button, Field, LinkButton, Notice, Segmented, SelectInput, TextInput } from '@/ui';

const KINDS = [{ value: 'person' as const, label: 'Физлицо' }, { value: 'org' as const, label: 'Юрлицо' }];
const EMPTY_PASSPORT = { series: '', number: '', issuedAt: '', departmentCode: '' };

export function RegisterPage() {
  const navigate = useNavigate();
  const { data: abonents } = useApi(() => api.abonents.list(), []);
  const [kind, setKind] = useState<AbonentKind>('person');
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  const [email, setEmail] = useState('');
  const [birthDate, setBirthDate] = useState('');
  const [passport, setPassport] = useState(EMPTY_PASSPORT);
  const [inn, setInn] = useState('');
  const [kpp, setKpp] = useState('');
  const [address, setAddress] = useState<AddressValue>(EMPTY_ADDRESS);
  const [addResident, setAddResident] = useState(true);
  const [heat, setHeat] = useState<HeatChoice>('norm');
  const [water, setWater] = useState<WaterChoice>('none');
  const [meterNumber, setMeterNumber] = useState('');
  const [meterReading, setMeterReading] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const person = kind === 'person';

  const similar = useMemo(() => {
    const normalized = name.trim().toLowerCase();
    if (normalized.length < 3) return null;
    return abonents?.find((item) => item.name.toLowerCase().includes(normalized)) ?? null;
  }, [abonents, name]);

  // Тот же адрес уже у другого абонента: возможно, это повторная регистрация
  const sameAddress = useMemo(() => {
    if (!isAddressComplete(address)) return null;
    const street = plainName(`${address.street} ${address.house}`);
    return abonents?.find((item) => plainName(item.place) === plainName(address.place) && plainName(item.street) === street && (item.flat ?? '') === address.flat.trim()) ?? null;
  }, [abonents, address]);

  const reading = meterReading.trim() === '' ? 0 : parseReading(meterReading);
  const meterProblem = water !== 'meter' ? null : !meterNumber.trim() ? 'Укажите номер счётчика' : reading === null || reading < 0 ? 'Проверьте начальное показание' : null;
  const phoneCheck = checkPhone(phone);
  const missing = !name.trim() || !isAddressComplete(address) ? 'Заполните имя и адрес' : phoneCheck.error ? 'Проверьте телефон' : meterProblem;
  const passportFilled = Object.values(passport).some((value) => value.trim() !== '');

  const create = async () => {
    const request: RegisterAbonentRequest = {
      kind, name: name.trim(), ...address, flat: address.flat.trim(),
      ...(phoneCheck.phone ? { phone: phoneCheck.phone } : {}),
      ...(email.trim() ? { email: email.trim() } : {}),
      ...(person && birthDate ? { birthDate } : {}),
      ...(person && passportFilled ? { passport: { series: passport.series.trim(), number: passport.number.trim(), issuedAt: passport.issuedAt || null, departmentCode: passport.departmentCode.trim() || null } } : {}),
      ...(!person && inn.trim() ? { inn: inn.trim() } : {}),
      ...(!person && kpp.trim() ? { kpp: kpp.trim() } : {}),
      addResident: person && addResident,
      services: { heat, water, ...(water === 'meter' ? { meterNumber: meterNumber.trim(), meterReading: reading ?? 0 } : {}) },
    };
    setError(null);
    setSaving(true);
    try {
      const created = await api.abonents.register(request);
      navigate(`/abonents?id=${created.id}`);
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setSaving(false);
    }
  };

  const passportField = (key: keyof typeof EMPTY_PASSPORT) => (event: React.ChangeEvent<HTMLInputElement>) => setPassport((current) => ({ ...current, [key]: event.target.value }));

  return (
    <>
      <div className="bar">
        <h1 className="title">Новый абонент</h1>
        <Segmented label="Тип абонента" options={KINDS} value={kind} onChange={setKind} itemWidth={96} />
        {similar && (
          <div style={{ flex: '1 1 320px', minWidth: 0 }}>
            <Notice tone="warn">Похожий: {similar.name}, {similar.street}. <Link to={`/abonents?id=${similar.id}`}>Открыть</Link></Notice>
          </div>
        )}
      </div>
      <div className="scroll" style={{ background: 'var(--tint)', padding: '12px 16px' }}>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12, alignItems: 'start' }}>
          <section className="card stack">
            <h2 className="section-title">1. {person ? 'Человек' : 'Организация'}</h2>
            {person ? (
              <>
                <Field label="ФИО">{(id) => <TextInput id={id} autoFocus value={name} onChange={(event) => setName(event.target.value)} />}</Field>
                <PhoneField value={phone} onChange={setPhone} />
                <Field label="Дата рождения">{(id) => <TextInput id={id} type="date" max={new Date().toLocaleDateString('sv-SE')} value={birthDate} onChange={(event) => setBirthDate(event.target.value)} />}</Field>
                <Field label="E-mail, если есть">{(id) => <TextInput id={id} type="email" value={email} onChange={(event) => setEmail(event.target.value)} />}</Field>
                <details open={passportFilled || undefined}>
                  <summary style={{ minHeight: 32, fontWeight: 500, cursor: 'pointer' }}>Паспорт, если нужен для договора</summary>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 8, marginTop: 6 }}>
                    <Field label="Серия">{(id) => <TextInput id={id} autoComplete="off" inputMode="numeric" placeholder="50 01" maxLength={5} value={passport.series} onChange={passportField('series')} />}</Field>
                    <Field label="Номер">{(id) => <TextInput id={id} autoComplete="off" inputMode="numeric" placeholder="123456" maxLength={6} value={passport.number} onChange={passportField('number')} />}</Field>
                    <Field label="Когда выдан">{(id) => <TextInput id={id} type="date" value={passport.issuedAt} onChange={passportField('issuedAt')} />}</Field>
                    <Field label="Код подразделения">{(id) => <TextInput id={id} autoComplete="off" placeholder="540-001" maxLength={7} value={passport.departmentCode} onChange={passportField('departmentCode')} />}</Field>
                  </div>
                  <p className="small muted" style={{ marginTop: 6, lineHeight: 1.4 }}>Хранится зашифрованным. Посмотреть может только администратор, каждый просмотр записывается в журнал.</p>
                </details>
              </>
            ) : (
              <>
                <Field label="ИНН" hint="Позже здесь будет поиск организации по ИНН.">{(id) => <TextInput id={id} autoFocus inputMode="numeric" value={inn} onChange={(event) => setInn(event.target.value)} />}</Field>
                <Field label="КПП">{(id) => <TextInput id={id} inputMode="numeric" maxLength={9} value={kpp} onChange={(event) => setKpp(event.target.value)} />}</Field>
                <Field label="Название">{(id) => <TextInput id={id} value={name} onChange={(event) => setName(event.target.value)} />}</Field>
                <PhoneField value={phone} onChange={setPhone} />
                <Field label="E-mail, если есть">{(id) => <TextInput id={id} type="email" value={email} onChange={(event) => setEmail(event.target.value)} />}</Field>
              </>
            )}
          </section>

          <section className="card stack">
            <h2 className="section-title">2. Адрес услуг</h2>
            <AddressPicker value={address} onChange={setAddress} />
            {sameAddress && <Notice tone="warn">По этому адресу уже есть абонент: {sameAddress.name}, ЛС {sameAddress.account}. <Link to={`/abonents?id=${sameAddress.id}`}>Открыть</Link></Notice>}
            {person && (
              <label className="row" style={{ gap: 8, minHeight: 40, fontWeight: 500 }}>
                <input type="checkbox" className="check" style={{ width: 20, height: 20 }} checked={addResident} onChange={(event) => setAddResident(event.target.checked)} />
                Добавить в проживающие
              </label>
            )}
            <p className="small muted" style={{ lineHeight: 1.45 }}>Улицы и дома берутся из справочников. Нет нужной — нажмите «Новая» или «Новый» рядом со списком.</p>
          </section>

          <section className="card stack">
            <h2 className="section-title">3. Услуги</h2>
            <Field label="Теплоснабжение">{(id) => (
              <SelectInput id={id} value={heat} onChange={(event) => setHeat(event.target.value as HeatChoice)}>
                <option value="norm">По нормативу</option><option value="none">Не подключать</option>
              </SelectInput>
            )}</Field>
            <Field label="Холодная вода">{(id) => (
              <SelectInput id={id} value={water} onChange={(event) => setWater(event.target.value as WaterChoice)}>
                <option value="none">Не подключать</option><option value="meter">По счётчику</option><option value="norm">По нормативу</option>
              </SelectInput>
            )}</Field>
            {water === 'meter' && (
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 8 }}>
                <Field label="Номер счётчика">{(id) => <TextInput id={id} mono value={meterNumber} onChange={(event) => setMeterNumber(event.target.value)} />}</Field>
                <Field label="Начальное показание">{(id) => <TextInput id={id} mono inputMode="decimal" placeholder="0" value={meterReading} onChange={(event) => setMeterReading(event.target.value)} />}</Field>
              </div>
            )}
            <p className="small muted" style={{ lineHeight: 1.45 }}>Тариф услуги выбирается в карточке абонента кнопкой «Тариф услуги».</p>
          </section>
        </div>
      </div>
      <div className="bar bar-end">
        {error && <span className="warn small" style={{ marginRight: 'auto' }}>{error}</span>}
        <LinkButton to="/abonents">Отмена</LinkButton>
        <Button variant="primary" size="auto" style={{ width: 220 }} disabled={Boolean(missing) || saving} onClick={() => void create()}>{missing ?? 'Создать абонента'}</Button>
      </div>
    </>
  );
}
