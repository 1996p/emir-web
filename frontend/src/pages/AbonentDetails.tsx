import { useEffect, useState } from 'react';
import { api, errorText } from '@/api';
import { formatPeriod, nextPeriod, parsePrice } from '@/domain/tariffs';
import type { Abonent, AbonentDetails as Details } from '@/domain/types';
import { useRole } from '@/layout/role';
import { AbonentBenefits } from './BenefitsDirectory';
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

  const save = () => run(async () => {
    if (phone.trim() !== (details.phone ?? '')) await api.abonents.setContact(abonent.id, phone.trim() || null);
    await api.abonents.setLegal(abonent.id, consent === '' ? null : consent === 'true', document.trim() || null);
  }, 'Сохранено.');

  return (
    <Section title="Контакты и документы">
      <p className="small muted">ЛС {abonent.account}{abonent.inn ? `, ИНН ${abonent.inn}` : ''}</p>
      <Field label="Телефон">{(id) => <TextInput id={id} readOnly={!canEdit} value={phone} onChange={(event) => setPhone(event.target.value)} />}</Field>
      <Field label="Согласие на передачу персональных данных" hint={details.consentWhen ? `Изменено ${details.consentWhen}` : undefined}>
        {(id) => <SelectInput id={id} disabled={!canEdit} value={consent} onChange={(event) => setConsent(event.target.value)}><option value="">Не указано</option><option value="false">Нет</option><option value="true">Да</option></SelectInput>}
      </Field>
      <Field label="Документ о праве собственности на отапливаемое помещение">{(id) => <TextArea id={id} rows={3} readOnly={!canEdit} value={document} onChange={(event) => setDocument(event.target.value)} />}</Field>
      <p className="small muted">Категория дома и норма теплопотребления появятся вместе с данными о домах.</p>
      {view}
      {canEdit && <Button size="full" onClick={() => void save()}>Сохранить</Button>}
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
