import { useEffect, useState } from 'react';
import { api, errorText, useApi, METER_UNITS } from '@/api';
import { formatPeriod, nextPeriod, parsePrice } from '@/domain/tariffs';
import type { Abonent, AbonentDetails as Details } from '@/domain/types';
import { useRole } from '@/layout/role';
import { AbonentBenefits } from './BenefitsDirectory';
import { PhoneField } from './PhoneField';
import { checkPhone } from '@/domain/phone';
import { Button, Field, Notice, SelectInput, TextArea, TextInput } from '@/ui';

function useAction() {
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const run = async (action: () => Promise<unknown>, done: string) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setNote(done);
      return true;
    } catch (reason) {
      setError(errorText(reason));
      return false;
    }
  };
  const view = (
    <>
      {error && <Notice tone="warn">{error}</Notice>}
      {note && <Notice tone="ok">{note}</Notice>}
    </>
  );
  return { run, view };
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="card stack" style={{ padding: 14, gap: 10, minWidth: 0 }}>
      <h2 className="section-title">{title}</h2>
      {children}
    </section>
  );
}

function CalculationSection({ abonent, details, canEdit }: { abonent: Abonent; details: Details; canEdit: boolean }) {
  const defaultMonth = details.reopened ? details.openPeriod : nextPeriod(details.openPeriod);
  const [fromPeriod, setFromPeriod] = useState(defaultMonth);
  const [area, setArea] = useState(String(abonent.area).replace('.', ','));
  const [residents, setResidents] = useState(String(abonent.residents));
  const [reason, setReason] = useState('');
  const { run, view } = useAction();
  const parsedArea = area.trim() === '0' ? 0 : parsePrice(area);
  const valid = parsedArea !== null && /^\d+$/.test(residents) && reason.trim() !== '' && fromPeriod !== '';

  return (
    <Section title="Параметры расчёта">
      <p className="small muted" style={{ lineHeight: 1.45 }}>Сейчас: {abonent.area} м², жильцов {abonent.residents}. Новые значения действуют с выбранного месяца, прошлые месяцы не меняются.</p>
      {canEdit && (
        <>
          <div className="row" style={{ alignItems: 'flex-start' }}>
            <Field label="Площадь, м²">{(id) => <TextInput id={id} mono inputMode="decimal" value={area} onChange={(event) => setArea(event.target.value)} />}</Field>
            <Field label="Жильцов">{(id) => <TextInput id={id} mono inputMode="numeric" value={residents} onChange={(event) => setResidents(event.target.value)} />}</Field>
          </div>
          <Field label="Действует с месяца">{(id) => <TextInput id={id} type="month" min={details.openPeriod} value={fromPeriod} onChange={(event) => setFromPeriod(event.target.value)} />}</Field>
          <Field label="Причина">{(id) => <TextInput id={id} placeholder="Например, выписка из ЕГРН" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
          {view}
          <Button size="full" disabled={!valid} onClick={() => void run(() => api.abonents.setFact(abonent.id, { fromPeriod, area: parsedArea ?? 0, residents: Number(residents), reason: reason.trim() }), `Сохранено, действует с месяца: ${formatPeriod(fromPeriod)}.`).then((ok) => { if (ok) setReason(''); })}>Сохранить</Button>
        </>
      )}
      <h3 className="field-label" style={{ marginTop: 4 }}>История</h3>
      {details.facts.map((fact) => (
        <div key={fact.fromPeriod} className="small" style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 6 }}>
          <div className="between"><span className="strong">{formatPeriod(fact.fromPeriod)}</span><span className="mono">{fact.area} м², жильцов {fact.residents}</span></div>
          <div className="muted">{fact.who}, {fact.when}{fact.reason ? `. ${fact.reason}` : ''}</div>
        </div>
      ))}
    </Section>
  );
}

function ResidentsSection({ abonent, details, canEdit }: { abonent: Abonent; details: Details; canEdit: boolean }) {
  const [name, setName] = useState('');
  const [fromDate, setFromDate] = useState('');
  const [endingId, setEndingId] = useState<number | null>(null);
  const [toDate, setToDate] = useState('');
  const { run, view } = useAction();
  const active = details.residents.filter((resident) => !resident.toDate).length;

  return (
    <Section title={`Проживающие: ${active}`}>
      {details.residents.length > 0 && active !== abonent.residents && <Notice tone="warn">В списке {active}, а в расчёте {abonent.residents}. Расчёт идёт по значению из параметров.</Notice>}
      {details.residents.length === 0 && <p className="small muted">Список пока не ведётся. Расчёт идёт по числу жильцов из параметров.</p>}
      {details.residents.map((resident) => (
        <div key={resident.id} className="small stack" style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 6, gap: 6 }}>
          <div className="between">
            <span className={resident.toDate ? 'muted' : 'strong'}>{resident.name}</span>
            {canEdit && !resident.toDate && endingId !== resident.id && <button type="button" className="link-button" onClick={() => { setEndingId(resident.id); setToDate(''); }}>Выбыл</button>}
          </div>
          <div className="muted">{resident.fromDate ? `с ${resident.fromDate}` : 'дата вселения не указана'}{resident.toDate ? `, выбыл ${resident.toDate}` : ''}</div>
          {endingId === resident.id && (
            <div className="row">
              <TextInput aria-label="Дата выбытия" type="date" value={toDate} onChange={(event) => setToDate(event.target.value)} />
              <Button size="sm" disabled={!toDate} onClick={() => void run(() => api.abonents.endResident(abonent.id, resident.id, toDate), 'Отмечено выбытие.').then((ok) => { if (ok) setEndingId(null); })}>Сохранить</Button>
            </div>
          )}
        </div>
      ))}
      {canEdit && (
        <>
          <Field label="ФИО">{(id) => <TextInput id={id} value={name} onChange={(event) => setName(event.target.value)} />}</Field>
          <Field label="Дата вселения">{(id) => <TextInput id={id} type="date" value={fromDate} onChange={(event) => setFromDate(event.target.value)} />}</Field>
          {view}
          <Button size="full" disabled={!name.trim()} onClick={() => void run(() => api.abonents.addResident(abonent.id, name.trim(), fromDate || null), 'Проживающий добавлен.').then((ok) => { if (ok) { setName(''); setFromDate(''); } })}>Добавить проживающего</Button>
        </>
      )}
    </Section>
  );
}

function ContactSection({ abonent, details, canEdit }: { abonent: Abonent; details: Details; canEdit: boolean }) {
  const [phone, setPhone] = useState(details.phone ?? '');
  const [consent, setConsent] = useState(details.consent === null ? '' : String(details.consent));
  const [document, setDocument] = useState(details.ownershipDocument ?? '');
  const { run, view } = useAction();

  useEffect(() => {
    setPhone(details.phone ?? '');
    setConsent(details.consent === null ? '' : String(details.consent));
    setDocument(details.ownershipDocument ?? '');
  }, [details.phone, details.consent, details.ownershipDocument]);

  const phoneCheck = checkPhone(phone);
  const save = () => run(async () => {
    if ((phoneCheck.phone ?? '') !== (details.phone ?? '')) await api.abonents.setContact(abonent.id, phoneCheck.phone);
    await api.abonents.setLegal(abonent.id, consent === '' ? null : consent === 'true', document.trim() || null);
  }, 'Сохранено.');

  return (
    <Section title="Контакты и документы">
      <p className="small muted">ЛС {abonent.account}{abonent.inn ? `, ИНН ${abonent.inn}` : ''}{details.kpp ? `, КПП ${details.kpp}` : ''}</p>
      {(details.email || details.birthDate || details.hasPassport) && (
        <div className="small" style={{ lineHeight: 1.6 }}>
          {details.birthDate && <div><span className="muted">Дата рождения: </span>{details.birthDate}</div>}
          {details.email && <div><span className="muted">E-mail: </span>{details.email}</div>}
          {details.hasPassport && <div><span className="muted">Паспорт: </span>заполнен, смотрит администратор в справочнике «Люди»</div>}
        </div>
      )}
      <PhoneField value={phone} onChange={setPhone} readOnly={!canEdit} />
      <Field label="Согласие на передачу персональных данных" hint={details.consentWhen ? `Изменено ${details.consentWhen}` : undefined}>
        {(id) => <SelectInput id={id} disabled={!canEdit} value={consent} onChange={(event) => setConsent(event.target.value)}><option value="">Не указано</option><option value="false">Нет</option><option value="true">Да</option></SelectInput>}
      </Field>
      <Field label="Документ о праве собственности на отапливаемое помещение">{(id) => <TextArea id={id} rows={3} readOnly={!canEdit} value={document} onChange={(event) => setDocument(event.target.value)} />}</Field>
      {view}
      {canEdit && <Button size="full" disabled={Boolean(phoneCheck.error)} onClick={() => void save()}>Сохранить</Button>}
    </Section>
  );
}

const formatNorm = (value: number | null) => (value === null ? 'не задана' : `${value.toLocaleString('ru-RU', { maximumFractionDigits: 6 })} Гкал/м²`);

/** Норма теплопотребления дома: по ней тариф «по норме дома» считает объём = площадь × норма. */
function HeatNormSection({ abonent, details, canEdit }: { abonent: Abonent; details: Details; canEdit: boolean }) {
  const [current, setCurrent] = useState(details.houseHeatNorm);
  const [norm, setNorm] = useState(details.houseHeatNorm === null ? '' : String(details.houseHeatNorm).replace('.', ','));
  const [reason, setReason] = useState('');
  const { run, view } = useAction();
  const text = norm.trim().replace(',', '.');
  const parsed = text === '' ? null : /^\d+(\.\d{1,6})?$/.test(text) && Number(text) > 0 ? Number(text) : undefined;
  const valid = parsed !== undefined && reason.trim() !== '';
  const volume = current === null ? null : Math.round(abonent.area * current * 10000) / 10000;

  useEffect(() => {
    setCurrent(details.houseHeatNorm);
    setNorm(details.houseHeatNorm === null ? '' : String(details.houseHeatNorm).replace('.', ','));
  }, [details.houseHeatNorm, abonent.id]);

  const save = () => run(() => api.abonents.setHeatNorm(abonent.id, parsed ?? null, reason.trim()), 'Норматив сохранён. Нажмите «Начислить», чтобы пересчитать открытый месяц.')
    .then((ok) => { if (ok) { setCurrent(parsed ?? null); setReason(''); } });

  return (
    <Section title="Норматив потребления">
      <HouseCategoryField abonent={abonent} details={details} canEdit={canEdit} onNorm={(value) => { setCurrent(value); setNorm(value === null ? '' : String(value).replace('.', ',')); }} />
      <p className="small muted" style={{ lineHeight: 1.45 }}>
        Сейчас: {formatNorm(current)}{volume !== null ? ` · за месяц ${abonent.area} м² × норма = ${volume.toLocaleString('ru-RU')} Гкал` : ''}.
        Действует для тарифа по норме дома, учитывается при следующем начислении.
      </p>
      {canEdit && (
        <>
          <Field label="Норма теплопотребления, Гкал/м²" hint="Пусто — норма не задана">{(id) => <TextInput id={id} mono inputMode="decimal" placeholder="0,01875" value={norm} onChange={(event) => setNorm(event.target.value)} />}</Field>
          <Field label="Причина">{(id) => <TextInput id={id} placeholder="Например, постановление о нормативах" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
          {parsed === undefined && norm.trim() !== '' && <Notice tone="warn">Введите число больше нуля, до 6 знаков после запятой.</Notice>}
          {view}
          <Button size="full" disabled={!valid} onClick={() => void save()}>Сохранить норматив</Button>
        </>
      )}
    </Section>
  );
}

/** Категория дома — из справочника «Категории домов». При смене абоненту ставится норма категории. */
function HouseCategoryField({ abonent, details, canEdit, onNorm }: { abonent: Abonent; details: Details; canEdit: boolean; onNorm: (norm: number | null) => void }) {
  const saved = details.houseCategory ?? '';
  const [current, setCurrent] = useState(saved);
  const [category, setCategory] = useState(saved);
  const [reason, setReason] = useState('');
  const { run, view } = useAction();
  const { data: options } = useApi(() => (canEdit ? api.directories.options('Категории домов', 'Название') : Promise.resolve([] as string[])), [canEdit]);
  const list = options ?? [];

  useEffect(() => { setCurrent(saved); setCategory(saved); }, [saved, abonent.id]);

  const save = () => run(() => api.abonents.setHouseCategory(abonent.id, category || null, reason.trim()), 'Категория сохранена. Нажмите «Начислить», чтобы пересчитать открытый месяц.')
    .then(async (ok) => {
      if (!ok) return;
      setCurrent(category);
      setReason('');
      onNorm((await api.abonents.details(abonent.id)).houseHeatNorm);
    });

  if (!canEdit) return <p className="small muted">Категория дома: {current || 'не указана'}.</p>;
  return (
    <>
      <Field label="Категория дома" hint="Из справочника «Категории домов». Норма категории проставится абоненту">{(id) => (
        <SelectInput id={id} value={category} onChange={(event) => setCategory(event.target.value)}>
          <option value="">— не указана —</option>
          {category && !list.includes(category) && <option value={category}>{category} (вне справочника)</option>}
          {list.map((item) => <option key={item} value={item}>{item}</option>)}
        </SelectInput>
      )}</Field>
      {category !== current && (
        <>
          <Field label="Причина смены категории">{(id) => <TextInput id={id} placeholder="Например, акт обследования" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
          {view}
          <Button size="full" disabled={!reason.trim()} onClick={() => void save()}>Сохранить категорию</Button>
        </>
      )}
    </>
  );
}

/** Квартирный счётчик одной услуги: установка, замена, снятие и единица показаний. */
function ApartmentMeter({ abonent, line, canEdit, run }: { abonent: Abonent; line: Abonent['services'][number]; canEdit: boolean; run: ReturnType<typeof useAction>['run'] }) {
  const [open, setOpen] = useState(false);
  const [number, setNumber] = useState('');
  const [reading, setReading] = useState('');
  const [verified, setVerified] = useState('');
  const [unit, setUnit] = useState('');
  const meter = line.meter;
  const parsedReading = reading.trim() === '' ? 0 : parsePrice(reading);
  const valid = number.trim() !== '' && parsedReading !== null;

  const save = () => run(() => api.abonents.setMeter(abonent.id, line.id, { number: number.trim(), lastReading: parsedReading ?? 0, verifiedUntil: verified.trim() || null, unit: unit || null, average: null }), meter ? 'Счётчик заменён.' : 'Счётчик установлен.')
    .then((ok) => { if (ok) { setOpen(false); setNumber(''); setReading(''); setVerified(''); setUnit(''); } });

  return (
    <div className="stack" style={{ gap: 6, borderBottom: '1px solid var(--line-soft)', paddingBottom: 8 }}>
      <div className="between"><span className="strong">{line.name}</span>
        {meter ? <span className="small muted mono">счётчик {meter.number}, показание {meter.lastReading}</span> : <span className="small muted">нет счётчика</span>}
      </div>
      {meter && (
        <Field label="Единица показаний" hint="ГДж, МВт·ч, кВт·ч переводятся в Гкал. «В единицах тарифа» — без перевода (вода в м³)">{(id) => (
          <SelectInput id={id} disabled={!canEdit} value={meter.unit ?? ''} onChange={(event) => void run(() => api.abonents.setMeterUnit(abonent.id, line.id, event.target.value || null), 'Единица сохранена, показание месяца пересчитано.')}>
            <option value="">В единицах тарифа</option>
            {METER_UNITS.map((u) => <option key={u.value} value={u.value}>{u.label}</option>)}
          </SelectInput>
        )}</Field>
      )}
      {canEdit && !open && (
        <div className="row">
          <Button size="sm" onClick={() => setOpen(true)}>{meter ? 'Заменить счётчик' : 'Установить счётчик'}</Button>
          {meter && <Button size="sm" onClick={() => void run(() => api.abonents.removeMeter(abonent.id, line.id), 'Счётчик снят.')}>Снять</Button>}
        </div>
      )}
      {canEdit && open && (
        <div className="stack" style={{ gap: 6 }}>
          <Field label="Номер счётчика">{(id) => <TextInput id={id} mono value={number} onChange={(event) => setNumber(event.target.value)} />}</Field>
          <Field label="Начальное показание">{(id) => <TextInput id={id} mono inputMode="decimal" placeholder="0" value={reading} onChange={(event) => setReading(event.target.value)} />}</Field>
          <Field label="Поверка до" hint="Например, 05.2029">{(id) => <TextInput id={id} value={verified} onChange={(event) => setVerified(event.target.value)} />}</Field>
          <Field label="Единица показаний">{(id) => <SelectInput id={id} value={unit} onChange={(event) => setUnit(event.target.value)}><option value="">В единицах тарифа</option>{METER_UNITS.map((u) => <option key={u.value} value={u.value}>{u.label}</option>)}</SelectInput>}</Field>
          <div className="row"><Button variant="primary" size="sm" disabled={!valid} onClick={() => void save()}>Сохранить</Button><Button size="sm" onClick={() => setOpen(false)}>Отмена</Button></div>
        </div>
      )}
    </div>
  );
}

/** Счётчики абонента: квартирные (установка/замена/единица) и привязка к общедомовому. */
function MetersSection({ abonent, canEdit }: { abonent: Abonent; canEdit: boolean }) {
  const { run, view } = useAction();
  const { data: houseMeters } = useApi(() => api.readings.houseMeters(), []);
  const lines = abonent.services.filter((line) => line.id !== 'penalty');

  return (
    <Section title="Счётчики">
      {lines.length === 0 && <p className="small muted">Услуг нет.</p>}
      {lines.map((line) => <ApartmentMeter key={line.id} abonent={abonent} line={line} canEdit={canEdit} run={run} />)}
      <h3 className="field-label" style={{ marginTop: 4 }}>Общедомовые счётчики</h3>
      {lines.map((line) => (
        <Field key={`house-${line.id}`} label={`${line.name}`} hint="Для тарифа «по общедомовому счётчику»">{(id) => (
          <SelectInput id={id} disabled={!canEdit || !houseMeters} value={line.houseMeterId ?? ''} onChange={(event) => void run(() => api.abonents.setHouseMeter(abonent.id, line.id, event.target.value || null), 'Привязка сохранена.')}>
            <option value="">Не привязан</option>
            {(houseMeters?.rows ?? []).map((meter) => <option key={meter.id} value={meter.id}>{meter.place}, {meter.street}{meter.number ? `, № ${meter.number}` : ''} ({meter.unit})</option>)}
          </SelectInput>
        )}</Field>
      ))}
      {view}
    </Section>
  );
}

/** Вкладка карточки с настоящими данными абонента: параметры расчёта, жильцы, контакты и документы. */
export function AbonentDetailsTab({ abonent, details }: { abonent: Abonent; details: Details | null }) {
  const role = useRole();
  if (!details) return null;
  const canEdit = role !== 'cashier';
  return (
    <div className="scroll pad" style={{ flex: '1 1 auto', display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: 12, alignItems: 'start', alignContent: 'start' }}>
      <CalculationSection key={`${abonent.id}-${details.openPeriod}-${details.reopened}`} abonent={abonent} details={details} canEdit={canEdit} />
      <HeatNormSection abonent={abonent} details={details} canEdit={canEdit} />
      <MetersSection abonent={abonent} canEdit={canEdit} />
      <ResidentsSection abonent={abonent} details={details} canEdit={canEdit} />
      <ContactSection abonent={abonent} details={details} canEdit={canEdit} />
      {canEdit && <AbonentBenefits abonentId={abonent.id} />}
    </div>
  );
}

/** Заметка видна на любой вкладке: в ней часто рабочие договорённости по абоненту. */
export function AbonentNote({ abonent, note }: { abonent: Abonent; note: string | null }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(note ?? '');
  const { run, view } = useAction();
  useEffect(() => { setText(note ?? ''); setEditing(false); }, [note, abonent.id]);

  if (!editing) {
    return (
      <div className="bar" style={{ background: note ? 'var(--warn-bg)' : undefined, padding: '6px 16px', justifyContent: 'space-between', flexWrap: 'nowrap' }}>
        <span className={note ? '' : 'small muted'} style={{ color: note ? 'var(--warn-ink)' : undefined, whiteSpace: 'pre-wrap', minWidth: 0 }}>{note ?? 'Заметки нет'}</span>
        <button type="button" className="link-button" style={{ flex: '0 0 auto' }} onClick={() => setEditing(true)}>{note ? 'Изменить' : 'Добавить заметку'}</button>
      </div>
    );
  }
  return (
    <div className="bar" style={{ alignItems: 'flex-start' }}>
      <div style={{ flex: '1 1 320px', minWidth: 0 }}><TextArea aria-label="Заметка" autoFocus rows={2} maxLength={2000} value={text} onChange={(event) => setText(event.target.value)} style={{ width: '100%' }} /></div>
      <Button size="sm" onClick={() => void run(() => api.abonents.setNote(abonent.id, text.trim() || null), '')}>Сохранить</Button>
      <Button size="sm" onClick={() => { setEditing(false); setText(note ?? ''); }}>Отмена</Button>
      {view}
    </div>
  );
}
