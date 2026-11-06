import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { DocumentCounter, DocumentRow, OrganizationProfile } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, DataTable, Field, Notice, SelectInput, TextInput, type Column } from '@/ui';

const KINDS = [['', 'Все виды'], ['receipt', 'Квитанции'], ['bill', 'Счета'], ['act', 'Акты'], ['facture', 'Счета-фактуры']] as const;
const printUrl = (ids: number[]) => `/print/documents?ids=${ids.join(',')}`;

const COLUMNS: Column<DocumentRow>[] = [
  { key: 'kind', header: 'Документ', render: (row) => row.kindName },
  { key: 'number', header: 'Номер', mono: true, render: (row) => `${row.number}${row.revision > 1 ? ` (испр. ${row.revision - 1})` : ''}` },
  { key: 'date', header: 'Дата', render: (row) => row.date },
  { key: 'abonent', header: 'Абонент', render: (row) => <span className="strong">{row.abonent}</span> },
  { key: 'total', header: 'Сумма, ₽', align: 'right', mono: true, render: (row) => formatMoney(row.total) },
  { key: 'status', header: 'Состояние', render: (row) => (row.stale ? <span className="warn strong">Нужно перевыпустить</span> : 'Действует') },
];

export function DocumentsPage() {
  const role = useRole();
  const [params] = useSearchParams();
  const canManage = role === 'accountant' || role === 'admin';
  const [period, setPeriod] = useState<string | undefined>(undefined);
  const [kind, setKind] = useState('');
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [panel, setPanel] = useState<'document' | 'settings'>('document');
  const [message, setMessage] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [staleReason, setStaleReason] = useState('');
  const abonent = params.get('abonent') ?? '';
  const { data, error } = useApi(() => api.documents.list(period, kind), [period, kind]);

  if (!data) return error ? <div className="pad"><Notice tone="warn">{error}</Notice></div> : null;
  const rows = abonent ? data.documents.filter((row) => row.abonentId === abonent) : data.documents;
  const selected = rows.find((row) => row.id === selectedId) ?? rows[0];

  const generate = async () => {
    setFailure(null);
    try {
      const result = await api.documents.generate(data.period);
      setMessage(`Создано документов: ${result.created}, уже было: ${result.existing}.`);
    } catch (reason) {
      setFailure(errorText(reason));
    }
  };

  const staleCount = data.documents.filter((row) => row.stale).length;
  const reissueStale = async () => {
    setFailure(null);
    try {
      const result = await api.documents.reissueStale(data.period, staleReason.trim());
      setMessage(`Перевыпущено документов: ${result.reissued}.`);
      setStaleReason('');
    } catch (reason) {
      setFailure(errorText(reason));
    }
  };

  return (
    <div className="split">
      <main className="main">
        <div className="bar">
          <h1 className="title">Документы</h1>
          <div style={{ width: 180 }}>
            <SelectInput aria-label="Месяц" value={data.period} onChange={(event) => { setPeriod(event.target.value); setSelectedId(null); setMessage(null); }}>
              {data.months.map((month) => <option key={month.period} value={month.period}>{month.title}</option>)}
            </SelectInput>
          </div>
          <div style={{ width: 170 }}>
            <SelectInput aria-label="Вид документа" value={kind} onChange={(event) => { setKind(event.target.value); setSelectedId(null); }}>
              {KINDS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
            </SelectInput>
          </div>
          <div className="spacer" />
          {canManage && <Button style={{ width: 170 }} size="auto" active={panel === 'settings'} onClick={() => setPanel(panel === 'settings' ? 'document' : 'settings')}>Реквизиты и номера</Button>}
          {rows.length > 0 && <a className="btn" style={{ width: 170 }} target="_blank" rel="noreferrer" href={printUrl(rows.map((row) => row.id))}>Печать списка: {rows.length}</a>}
          {canManage && <Button variant="primary" style={{ width: 170 }} size="auto" disabled={!data.canGenerate} title={data.cannotGenerate ?? undefined} onClick={() => void generate()}>Сформировать</Button>}
        </div>
        {canManage && staleCount > 0 && (
          <div className="bar" style={{ background: 'var(--warn-bg)' }}>
            <span className="strong" style={{ color: 'var(--warn-ink)' }}>Устарели после перезакрытия: {staleCount}</span>
            <div style={{ flex: '1 1 220px', minWidth: 0 }}><TextInput aria-label="Причина перевыпуска" placeholder="Причина перевыпуска" value={staleReason} onChange={(event) => setStaleReason(event.target.value)} /></div>
            <Button style={{ width: 170 }} size="auto" disabled={!staleReason.trim() || !data.canGenerate} onClick={() => void reissueStale()}>Перевыпустить все</Button>
          </div>
        )}
        {data.cannotGenerate && canManage && <div style={{ padding: '8px 16px' }}><Notice>{data.cannotGenerate}.</Notice></div>}
        {message && <div style={{ padding: '8px 16px' }}><Notice tone="ok">{message}</Notice></div>}
        {failure && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{failure}</Notice></div>}
        <DataTable columns={COLUMNS} rows={rows} rowKey={(row) => String(row.id)} minWidth={720} onRowClick={(row) => { setSelectedId(row.id); setPanel('document'); }} isSelected={(row) => row.id === selected?.id}
          empty={<span className="muted">Документов за этот месяц нет.</span>} />
      </main>
      {panel === 'settings' && canManage ? <SettingsPanel /> : selected ? <DocumentPanel key={selected.id} row={selected} canManage={canManage} canReissue={data.canGenerate} /> : null}
    </div>
  );
}

function DocumentPanel({ row, canManage, canReissue }: { row: DocumentRow; canManage: boolean; canReissue: boolean }) {
  const { data: history } = useApi(() => api.documents.history(row.abonentId, row.kind), [row.abonentId, row.kind, row.id]);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const versions = (history ?? []).filter((item) => item.period === row.period);

  const reissue = async () => {
    setError(null);
    try {
      await api.documents.reissue(row.id, reason.trim());
      setReason('');
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <aside className="pane pane-right" aria-label="Документ" style={{ width: 340 }}>
      <div className="scroll stack pad">
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>{row.kindName} № {row.number}</h2>
        <p className="muted">{row.abonent}, {row.periodTitle.toLowerCase()}</p>
        {row.stale && <Notice tone="warn">Месяц перезакрыли после выпуска. Суммы в документе устарели.</Notice>}
        <a className="btn" style={{ width: '100%' }} target="_blank" rel="noreferrer" href={printUrl([row.id])}>Открыть и напечатать</a>
        {versions.length > 1 && (
          <>
            <h3 className="section-title">Версии</h3>
            {versions.map((item) => (
              <div key={item.id} className="between small">
                <span>{item.revision === 1 ? 'Первичный' : `Исправление ${item.revision - 1}`}{item.status === 'replaced' ? ', заменён' : ''}{item.reason ? `. ${item.reason}` : ''}</span>
                <a target="_blank" rel="noreferrer" href={printUrl([item.id])}>Открыть</a>
              </div>
            ))}
          </>
        )}
        {canManage && (
          <>
            <h3 className="section-title" style={{ marginTop: 6 }}>Перевыпуск</h3>
            <p className="small muted" style={{ lineHeight: 1.45 }}>Номер сохранится, в документе появится номер исправления. Прежняя версия останется в истории.</p>
            <Field label="Причина">{(id) => <TextInput id={id} value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
            {error && <Notice tone="warn">{error}</Notice>}
            <Button size="full" disabled={!reason.trim() || !canReissue} onClick={() => void reissue()}>Перевыпустить</Button>
          </>
        )}
      </div>
    </aside>
  );
}

const EMPTY: OrganizationProfile = { name: '', inn: '', kpp: null, address: '', bankName: null, bik: null, corrAccount: null, account: null, director: null, chiefAccountant: null, vatRate: null, heatSeasonStartMonth: 10, heatSeasonEndMonth: 4 };
const MONTHS = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
const FIELDS: Array<[Exclude<keyof OrganizationProfile, 'vatRate'>, string]> = [['name', 'Название'], ['inn', 'ИНН'], ['kpp', 'КПП'], ['address', 'Адрес'], ['bankName', 'Банк'], ['bik', 'БИК'], ['corrAccount', 'Корр. счёт'], ['account', 'Расчётный счёт'], ['director', 'Руководитель'], ['chiefAccountant', 'Главный бухгалтер']];

function SettingsPanel() {
  const { data } = useApi(() => api.documents.profile(), []);
  const { data: counters } = useApi(() => api.documents.counters(), []);
  const [form, setForm] = useState<OrganizationProfile>(EMPTY);
  const [vat, setVat] = useState('');
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!data) return;
    setForm(data);
    setVat(data.vatRate === null ? '' : String(data.vatRate));
  }, [data]);

  const save = async () => {
    setError(null);
    setNote(null);
    try {
      await api.documents.saveProfile({ ...form, vatRate: vat.trim() === '' ? null : Number(vat.replace(',', '.')) });
      setNote('Реквизиты сохранены. Выпущенные документы не изменятся.');
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <aside className="pane pane-right" aria-label="Реквизиты и нумерация" style={{ width: 360 }}>
      <div className="scroll stack pad">
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>Реквизиты организации</h2>
        {FIELDS.map(([key, label]) => (
          <Field key={key} label={label}>{(id) => <TextInput id={id} value={form[key] ?? ''} onChange={(event) => setForm({ ...form, [key]: event.target.value })} />}</Field>
        ))}
        <Field label="НДС, %. Пусто: без НДС" hint="Считается как входящий в сумму начисления.">{(id) => <TextInput id={id} inputMode="decimal" value={vat} onChange={(event) => setVat(event.target.value)} />}</Field>
        <h2 className="section-title" style={{ marginTop: 8 }}>Отопительный сезон</h2>
        <p className="small muted" style={{ lineHeight: 1.45 }}>Для сезонных схем начисления. Вне этих месяцев сезонные схемы дают 0. Сезон переходит через год: октябрь — апрель.</p>
        <div className="row">
          <Field label="С месяца">{(id) => <SelectInput id={id} value={String(form.heatSeasonStartMonth)} onChange={(event) => setForm({ ...form, heatSeasonStartMonth: Number(event.target.value) })}>{MONTHS.map((name, index) => <option key={index} value={index + 1}>{name}</option>)}</SelectInput>}</Field>
          <Field label="По месяц включительно">{(id) => <SelectInput id={id} value={String(form.heatSeasonEndMonth)} onChange={(event) => setForm({ ...form, heatSeasonEndMonth: Number(event.target.value) })}>{MONTHS.map((name, index) => <option key={index} value={index + 1}>{name}</option>)}</SelectInput>}</Field>
        </div>
        {error && <Notice tone="warn">{error}</Notice>}
        {note && <Notice tone="ok">{note}</Notice>}
        <Button variant="primary" size="full" onClick={() => void save()}>Сохранить реквизиты</Button>
        <h2 className="section-title" style={{ marginTop: 8 }}>Нумерация</h2>
        <Counters counters={counters ?? []} />
      </div>
    </aside>
  );
}

function Counters({ counters }: { counters: DocumentCounter[] }) {
  const [year, setYear] = useState(String(new Date().getFullYear()));
  const [kind, setKind] = useState('bill');
  const [last, setLast] = useState('');
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    setError(null);
    try {
      await api.documents.setCounter({ year: Number(year), kind, last: Number(last) });
      setLast('');
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <>
      {counters.length === 0 && <p className="small muted">Номера ещё не выдавались. Первый документ получит № 1.</p>}
      {counters.map((item) => <div key={`${item.year}-${item.kind}`} className="between small"><span>{item.kindName}, {item.year}</span><span className="mono">последний № {item.last}</span></div>)}
      <div className="row">
        <Field label="Год">{(id) => <TextInput id={id} inputMode="numeric" value={year} onChange={(event) => setYear(event.target.value)} />}</Field>
        <Field label="Вид">{(id) => <SelectInput id={id} value={kind} onChange={(event) => setKind(event.target.value)}><option value="bill">Счёт</option><option value="act">Акт</option><option value="facture">Счёт-фактура</option></SelectInput>}</Field>
      </div>
      <Field label="Последний выданный номер" hint="Назад нумерацию не сдвинуть, номера не должны повторяться.">{(id) => <TextInput id={id} inputMode="numeric" value={last} onChange={(event) => setLast(event.target.value)} />}</Field>
      {error && <Notice tone="warn">{error}</Notice>}
      <Button size="full" disabled={!/^\d+$/.test(last)} onClick={() => void save()}>Задать номер</Button>
    </>
  );
}
