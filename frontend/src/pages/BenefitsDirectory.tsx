import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import type { Beneficiary, BenefitCategory, BenefitImportLine } from '@/domain/types';
import { useRole } from '@/layout/role';
import { formatMoney } from '@/domain/billing';
import { Button, DataTable, Field, Notice, SelectInput, TextInput, type Column } from '@/ui';

const IMPORT_STATES: Record<string, string> = { ok: 'Будет добавлен', 'not-found': 'Счёт не найден', 'unknown-category': 'Нет такой категории', duplicate: 'Уже есть', 'bad-date': 'Неверная дата', 'bad-name': 'Неверное ФИО' };

const BENEFICIARY_COLUMNS: Column<Beneficiary>[] = [
  { key: 'name', header: 'ФИО', render: (row) => <span className={row.active ? 'strong' : 'muted'}>{row.fullName}</span> },
  { key: 'category', header: 'Категория', render: (row) => row.category },
  { key: 'account', header: 'ЛС', mono: true, render: (row) => row.account },
  { key: 'address', header: 'Адрес', render: (row) => row.address },
  { key: 'state', header: 'Состояние', render: (row) => (row.active ? 'Активен' : `Не активен${row.toDate ? ` с ${row.toDate}` : ''}`) },
  { key: 'debtor', header: 'Должник', render: (row) => (row.debtor ? <span className="warn strong">Да, {formatMoney(row.debt)} ₽</span> : 'Нет') },
];

export function BeneficiariesDirectory({ filter }: { filter: string }) {
  const role = useRole();
  const navigate = useNavigate();
  const canExchange = role === 'accountant' || role === 'admin';
  const { data: rows, error } = useApi(() => api.benefits.list(), []);
  const [state, setState] = useState('active');
  const [onlyDebtors, setOnlyDebtors] = useState(false);
  const [panelOpen, setPanelOpen] = useState(false);
  const needle = filter.trim().toLowerCase();
  const visible = (rows ?? []).filter((row) => (state === 'all' || row.active) && (!onlyDebtors || row.debtor) && (!needle || `${row.fullName} ${row.category} ${row.account} ${row.address}`.toLowerCase().includes(needle)));

  return (
    <div className="split">
      <div className="main">
        <div className="bar">
          <div style={{ width: 170 }}><SelectInput aria-label="Состояние" value={state} onChange={(event) => setState(event.target.value)}><option value="active">Активные</option><option value="all">Все</option></SelectInput></div>
          <label className="small row" style={{ gap: 6 }}><input type="checkbox" checked={onlyDebtors} onChange={(event) => setOnlyDebtors(event.target.checked)} />Только должники</label>
          <div className="spacer" />
          <span className="small muted">Льготников: {visible.length}</span>
          {canExchange && <Button style={{ width: 170 }} size="auto" active={panelOpen} onClick={() => setPanelOpen(!panelOpen)}>Обмен с соцзащитой</Button>}
        </div>
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        <DataTable columns={BENEFICIARY_COLUMNS} rows={visible} rowKey={(row) => String(row.id)} minWidth={760} onRowClick={(row) => navigate(`/abonents?id=${encodeURIComponent(row.abonentId)}`)} empty={<span className="muted">Льготников нет. Добавить можно в карточке абонента или загрузить файлом.</span>} />
      </div>
      {panelOpen && canExchange && <ExchangePanel />}
    </div>
  );
}

function ExchangePanel() {
  const { data: months } = useApi(() => api.documents.list(), []);
  const [period, setPeriod] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [lines, setLines] = useState<BenefitImportLine[] | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const chosen = period || months?.period || '';
  const ready = lines?.filter((line) => line.state === 'ok').length ?? 0;

  const run = async (apply: boolean) => {
    if (!file) return;
    setError(null);
    setMessage(null);
    try {
      const result = await api.benefits.importFile(file, apply);
      setLines(apply ? null : result.lines);
      if (apply) { setMessage(`Добавлено льготников: ${result.added}.`); setFile(null); }
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <aside className="pane pane-right" aria-label="Обмен с соцзащитой" style={{ width: 360 }}>
      <div className="scroll stack pad">
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>Ответ соцзащите</h2>
        <p className="small muted" style={{ lineHeight: 1.45 }}>Начисления, оплаты и долги льготников за месяц. Выгрузка попадёт в журнал. Формат предварительный: образца от соцзащиты пока нет.</p>
        <Field label="Месяц">{(id) => <SelectInput id={id} value={chosen} onChange={(event) => setPeriod(event.target.value)}>{months?.months.map((month) => <option key={month.period} value={month.period}>{month.title}</option>)}</SelectInput>}</Field>
        <a className="btn" style={{ width: '100%' }} href={api.benefits.responseUrl(chosen)}>Скачать CSV</a>

        <h2 style={{ fontSize: 17, fontWeight: 600, marginTop: 8 }}>Загрузка льготников</h2>
        <p className="small muted" style={{ lineHeight: 1.45 }}>Файл CSV: <span className="mono">лицевой счёт;ФИО;категория;дата начала</span>. Сначала проверка, запись только после подтверждения.</p>
        <Field label="Файл">{(id) => <input id={id} type="file" accept=".csv,.txt" onChange={(event) => { setFile(event.target.files?.[0] ?? null); setLines(null); setMessage(null); }} />}</Field>
        <Button size="full" disabled={!file} onClick={() => void run(false)}>Проверить файл</Button>
        {error && <Notice tone="warn">{error}</Notice>}
        {message && <Notice tone="ok">{message}</Notice>}
        {lines && (
          <>
            {lines.map((line) => (
              <div key={line.line} className="small between" style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 4 }}>
                <span>{line.line}. {line.account}, {line.fullName}</span>
                <span className={line.state === 'ok' ? '' : 'warn strong'} style={{ flex: '0 0 auto' }}>{IMPORT_STATES[line.state] ?? line.state}</span>
              </div>
            ))}
            <Button variant="primary" size="full" disabled={ready === 0} onClick={() => void run(true)}>Добавить: {ready}</Button>
          </>
        )}
      </div>
    </aside>
  );
}

const CATEGORY_COLUMNS: Column<BenefitCategory>[] = [
  { key: 'name', header: 'Название', render: (row) => <span className={row.isActive ? 'strong' : 'muted'}>{row.name}</span> },
  { key: 'state', header: 'Состояние', render: (row) => (row.isActive ? 'Используется' : 'Не используется') },
  { key: 'count', header: 'Льготников', align: 'right', mono: true, render: (row) => row.beneficiaries },
];

export function CategoriesDirectory({ filter }: { filter: string }) {
  const role = useRole();
  const canEdit = role === 'accountant' || role === 'admin';
  const { data: rows, error: loadError } = useApi(() => api.benefits.categories(), []);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [name, setName] = useState('');
  const [active, setActive] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const needle = filter.trim().toLowerCase();
  const visible = (rows ?? []).filter((row) => !needle || row.name.toLowerCase().includes(needle));
  const selected = rows?.find((row) => row.id === selectedId) ?? null;

  useEffect(() => {
    setName(selected?.name ?? '');
    setActive(selected?.isActive ?? true);
    setError(null);
  }, [selected?.id, selected?.name, selected?.isActive]);

  const save = async () => {
    setError(null);
    try {
      if (selected) await api.benefits.saveCategory(selected.id, name.trim(), active);
      else { await api.benefits.addCategory(name.trim()); setName(''); }
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <div className="split">
      <div className="main">
        {loadError && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{loadError}</Notice></div>}
        <DataTable columns={CATEGORY_COLUMNS} rows={visible} rowKey={(row) => String(row.id)} onRowClick={(row) => setSelectedId(row.id)} isSelected={(row) => row.id === selectedId} empty={<span className="muted">Категорий нет.</span>} />
      </div>
      {canEdit && (
        <aside className="pane pane-right" aria-label="Категория" style={{ width: 320 }}>
          <div className="scroll stack pad">
            <h2 style={{ fontSize: 17, fontWeight: 600 }}>{selected ? 'Категория' : 'Новая категория'}</h2>
            <p className="small muted" style={{ lineHeight: 1.45 }}>Как и в старой системе, у категории только название: льгота не меняет сумму начисления.</p>
            <Field label="Название">{(id) => <TextInput id={id} value={name} onChange={(event) => setName(event.target.value)} />}</Field>
            {selected && <label className="small row" style={{ gap: 6 }}><input type="checkbox" checked={active} onChange={(event) => setActive(event.target.checked)} />Используется</label>}
            {error && <Notice tone="warn">{error}</Notice>}
            <Button variant="primary" size="full" disabled={!name.trim()} onClick={() => void save()}>{selected ? 'Сохранить' : 'Добавить'}</Button>
            {selected && <Button size="full" onClick={() => setSelectedId(null)}>Новая категория</Button>}
          </div>
        </aside>
      )}
    </div>
  );
}

/** Блок карточки абонента. Касса его не видит: категория льготы может раскрывать состояние здоровья. */
export function AbonentBenefits({ abonentId }: { abonentId: string }) {
  const { data: rows } = useApi(() => api.benefits.forAbonent(abonentId), [abonentId]);
  const { data: categories } = useApi(() => api.benefits.categories(), []);
  const [categoryId, setCategoryId] = useState('');
  const [fullName, setFullName] = useState('');
  const [fromDate, setFromDate] = useState('');
  const [endingId, setEndingId] = useState<number | null>(null);
  const [toDate, setToDate] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const usable = (categories ?? []).filter((item) => item.isActive);
  const chosen = categoryId || String(usable[0]?.id ?? '');

  const run = async (action: () => Promise<unknown>, after: () => void) => {
    setError(null);
    try {
      await action();
      after();
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <section className="card stack" style={{ padding: 14, gap: 10, minWidth: 0 }}>
      <h2 className="section-title">Льготы</h2>
      <p className="small muted" style={{ lineHeight: 1.45 }}>Сумму начисления не меняют: нужны для обмена с соцзащитой.</p>
      {(rows ?? []).length === 0 && <p className="small muted">Льгот нет.</p>}
      {rows?.map((row) => (
        <div key={row.id} className="small stack" style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 6, gap: 4 }}>
          <div className="between">
            <span className={row.active ? 'strong' : 'muted'}>{row.fullName}</span>
            {!row.toDate && endingId !== row.id && <button type="button" className="link-button" onClick={() => { setEndingId(row.id); setToDate(''); setReason(''); }}>Прекратить</button>}
          </div>
          <div className="muted">{row.category}{row.fromDate ? `, с ${row.fromDate}` : ''}{row.toDate ? `, до ${row.toDate}` : ''}</div>
          {endingId === row.id && (
            <>
              <TextInput aria-label="Дата окончания" type="date" value={toDate} onChange={(event) => setToDate(event.target.value)} />
              <TextInput aria-label="Причина" placeholder="Причина" value={reason} onChange={(event) => setReason(event.target.value)} />
              <div className="row">
                <Button size="sm" disabled={!toDate || !reason.trim()} onClick={() => void run(() => api.benefits.end(row.id, toDate, reason.trim()), () => setEndingId(null))}>Сохранить</Button>
                <Button size="sm" onClick={() => setEndingId(null)}>Отмена</Button>
              </div>
            </>
          )}
        </div>
      ))}
      <Field label="Категория">{(id) => <SelectInput id={id} value={chosen} onChange={(event) => setCategoryId(event.target.value)}>{usable.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</SelectInput>}</Field>
      <Field label="ФИО льготника">{(id) => <TextInput id={id} value={fullName} onChange={(event) => setFullName(event.target.value)} />}</Field>
      <Field label="Действует с">{(id) => <TextInput id={id} type="date" value={fromDate} onChange={(event) => setFromDate(event.target.value)} />}</Field>
      {error && <Notice tone="warn">{error}</Notice>}
      <Button size="full" disabled={!fullName.trim() || !chosen} onClick={() => void run(() => api.benefits.add({ abonentId, categoryId: Number(chosen), fullName: fullName.trim(), fromDate: fromDate || null }), () => { setFullName(''); setFromDate(''); })}>Добавить льготу</Button>
    </section>
  );
}
