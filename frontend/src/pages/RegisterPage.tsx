import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import type { AbonentKind } from '@/domain/types';
import { Button, Field, LinkButton, Notice, Segmented, SelectInput, TextInput } from '@/ui';

const KINDS = [{ value: 'person' as const, label: 'Физлицо' }, { value: 'org' as const, label: 'Юрлицо' }];

export function RegisterPage() {
  const navigate = useNavigate();
  const { data: abonents } = useApi(() => api.abonents.list(), []);
  const [kind, setKind] = useState<AbonentKind>('person');
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  const [inn, setInn] = useState('');
  const [address, setAddress] = useState('');
  const [meterService, setMeterService] = useState('По счётчику');
  const [error, setError] = useState<string | null>(null);

  const similar = useMemo(() => {
    const normalized = name.trim().toLowerCase();
    if (normalized.length < 3) return null;
    return abonents?.find((item) => item.name.toLowerCase().includes(normalized)) ?? null;
  }, [abonents, name]);

  const canCreate = name.trim().length > 0 && address.trim().length > 0;

  const create = async () => {
    const request = { kind, name: name.trim(), address: address.trim(), ...(phone.trim() ? { phone: phone.trim() } : {}), ...(inn.trim() ? { inn: inn.trim() } : {}) };
    setError(null);
    try {
      const created = await api.abonents.register(request);
      navigate(`/abonents?id=${created.id}`);
    } catch (reason) {
      setError(errorText(reason));
    }
  };

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
            <h2 className="section-title">1. {kind === 'person' ? 'Человек' : 'Организация'}</h2>
            {kind === 'person' ? (
              <>
                <Field label="ФИО">{(id) => <TextInput id={id} autoFocus value={name} onChange={(event) => setName(event.target.value)} />}</Field>
                <Field label="Телефон">{(id) => <TextInput id={id} type="tel" placeholder="+7" value={phone} onChange={(event) => setPhone(event.target.value)} />}</Field>
                <Field label="Дата рождения">{(id) => <TextInput id={id} type="date" />}</Field>
                <Field label="E-mail, если есть">{(id) => <TextInput id={id} type="email" />}</Field>
                <details>
                  <summary style={{ minHeight: 32, fontWeight: 500, cursor: 'pointer' }}>Паспорт, если нужен для договора</summary>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 8, marginTop: 6 }}>
                    <Field label="Серия">{(id) => <TextInput id={id} autoComplete="off" />}</Field>
                    <Field label="Номер">{(id) => <TextInput id={id} autoComplete="off" />}</Field>
                    <Field label="Когда выдан">{(id) => <TextInput id={id} type="date" />}</Field>
                    <Field label="Код подразделения">{(id) => <TextInput id={id} autoComplete="off" />}</Field>
                  </div>
                </details>
              </>
            ) : (
              <>
                <Field label="ИНН" hint="Позже здесь будет поиск организации по ИНН.">{(id) => <TextInput id={id} autoFocus value={inn} onChange={(event) => setInn(event.target.value)} />}</Field>
                <Field label="КПП">{(id) => <TextInput id={id} />}</Field>
                <Field label="Название">{(id) => <TextInput id={id} value={name} onChange={(event) => setName(event.target.value)} />}</Field>
                <Field label="Телефон">{(id) => <TextInput id={id} type="tel" placeholder="+7" value={phone} onChange={(event) => setPhone(event.target.value)} />}</Field>
              </>
            )}
          </section>

          <section className="card stack">
            <h2 className="section-title">2. Адрес услуг</h2>
            <Field label="Адрес">{(id) => <TextInput id={id} value={address} onChange={(event) => setAddress(event.target.value)} placeholder="Улица, дом, квартира" />}</Field>
            <label className="row" style={{ gap: 8, minHeight: 40, fontWeight: 500 }}>
              <input type="checkbox" className="check" style={{ width: 20, height: 20 }} defaultChecked disabled={kind === 'org'} />
              Добавить в проживающие
            </label>
            <Link to="/directories?name=Дома" className="small" style={{ fontWeight: 500 }}>Такого адреса нет</Link>
          </section>

          <section className="card stack">
            <h2 className="section-title">3. Услуги</h2>
            <Field label="Теплоснабжение">{(id) => <SelectInput id={id}><option>По нормативу</option><option>Не подключать</option></SelectInput>}</Field>
            <Field label="Холодная вода">{(id) => (
              <SelectInput id={id} value={meterService} onChange={(event) => setMeterService(event.target.value)}>
                <option>По счётчику</option><option>По нормативу</option><option>Не подключать</option>
              </SelectInput>
            )}</Field>
            {meterService === 'По счётчику' && (
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 8 }}>
                <Field label="Номер счётчика">{(id) => <TextInput id={id} />}</Field>
                <Field label="Начальное показание">{(id) => <TextInput id={id} mono inputMode="decimal" />}</Field>
              </div>
            )}
          </section>
        </div>
      </div>
      <div className="bar bar-end">
        {error && <span className="warn small" style={{ marginRight: 'auto' }}>{error}</span>}
        <LinkButton to="/abonents">Отмена</LinkButton>
        <Button variant="primary" size="auto" style={{ width: 200 }} disabled={!canCreate} onClick={() => void create()}>{canCreate ? 'Создать абонента' : 'Заполните имя и адрес'}</Button>
      </div>
    </>
  );
}
