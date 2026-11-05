import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { CancelLine } from '@/domain/types';
import { Button, DataTable, Field, Notice, Segmented, SelectInput, TextInput, type Column } from '@/ui';

const COLUMNS: Column<CancelLine>[] = [
  { key: 'abonent', header: 'Абонент', render: (row) => <span className="strong">{row.abonent}</span> },
  { key: 'account', header: 'ЛС', mono: true, render: (row) => row.account },
  { key: 'service', header: 'Услуга', render: (row) => row.service },
  { key: 'amount', header: 'Будет снято, ₽', align: 'right', mono: true, render: (row) => formatMoney(row.amount) },
];

type Who = '' | 'person' | 'org';
const WHO: Array<{ value: Who; label: string }> = [{ value: '', label: 'Все' }, { value: 'person', label: 'Физлица' }, { value: 'org', label: 'Юрлица' }];

export function CancelChargesPage() {
  const [kind, setKind] = useState<Who>('');
  const [place, setPlace] = useState('');
  const [query, setQuery] = useState('');
  const [picked, setPicked] = useState<string[] | null>(null);
  const [reason, setReason] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Первый запрос без услуг только получает список услуг, отмечать их должен сам бухгалтер
  const services = picked ?? [];
  const { data: preview, error: loadError } = useApi(() => api.period.cancelPreview({ kind, place, query, services }), [kind, place, query, services.join(',')]);

  const toggle = (id: string) => { setPicked(services.includes(id) ? services.filter((item) => item !== id) : [...services, id]); setConfirming(false); setMessage(null); };

  const apply = async () => {
    if (!preview) return;
    setError(null);
    try {
      const result = await api.period.cancel({ kind, place, query, services, reason: reason.trim(), expectedTotal: preview.total, expectedLines: preview.lines.length });
      setMessage(result.message);
      setReason('');
      setPicked([]);
    } catch (reason_) {
      setError(errorText(reason_));
    }
    setConfirming(false);
  };

  return (
    <div className="split">
      <main className="main">
        <div className="bar" style={{ justifyContent: 'space-between' }}>
          <h1 className="title">Отмена начислений{preview ? `: ${preview.title.toLowerCase()}` : ''}</h1>
          <Link to="/period" className="btn" style={{ width: 148 }}>К месяцу</Link>
        </div>
        {loadError && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{loadError}</Notice></div>}
        {message && <div style={{ padding: '8px 16px' }}><Notice tone="ok">{message}</Notice></div>}
        <DataTable columns={COLUMNS} rows={preview?.lines ?? []} rowKey={(row) => `${row.abonentId}-${row.serviceId}`} minWidth={560}
          empty={<span className="muted">{services.length === 0 ? 'Отметьте услуги справа, и здесь появится список.' : 'Под условия не попало ни одного начисления.'}</span>} />
      </main>
      <aside className="pane pane-right" aria-label="Условия отмены" style={{ width: 344 }}>
        <div className="scroll stack pad">
          <h2 className="section-title">Услуги</h2>
          {preview?.services.length === 0 && <p className="small muted">За этот месяц начислений нет.</p>}
          {preview?.services.map((service) => (
            <label key={service.id} className="between small" style={{ gap: 8, cursor: 'pointer' }}>
              <span className="row" style={{ gap: 6 }}><input type="checkbox" checked={services.includes(service.id)} onChange={() => toggle(service.id)} />{service.name}</span>
              <span className="mono">{formatMoney(service.total)} ₽</span>
            </label>
          ))}
          <h2 className="section-title" style={{ marginTop: 6 }}>Кому</h2>
          <Segmented label="Тип абонента" options={WHO} value={kind} onChange={(value) => { setKind(value); setConfirming(false); }} itemWidth={92} />
          <Field label="Населённый пункт">{(id) => <SelectInput id={id} value={place} onChange={(event) => { setPlace(event.target.value); setConfirming(false); }}><option value="">Все</option>{preview?.places.map((item) => <option key={item}>{item}</option>)}</SelectInput>}</Field>
          <Field label="ФИО, лицевой счёт или адрес">{(id) => <TextInput id={id} type="search" value={query} onChange={(event) => { setQuery(event.target.value); setConfirming(false); }} />}</Field>
          <Field label="Причина">{(id) => <TextInput id={id} placeholder="Например, неверная цена в приказе" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
          <p className="small muted" style={{ lineHeight: 1.45 }}>Показания счётчиков останутся. Оплаченная часть станет переплатой. Шаги месяца начнутся заново: запустите начисление ещё раз.</p>
          {preview?.penaltyDone && !services.includes('penalty') && <Notice tone="warn">Пени уже начислены и зависят от долга. Запустите их заново после начисления.</Notice>}
          {error && <Notice tone="warn">{error}</Notice>}
        </div>
        <div className="stack pad" style={{ background: 'var(--surface)', borderTop: '1px solid var(--line)', gap: 8 }}>
          {preview && <div className="between strong"><span>Строк: {preview.lines.length}</span><span className="mono">{formatMoney(preview.total)} ₽</span></div>}
          {confirming && preview ? (
            <>
              <Notice tone="warn"><strong style={{ display: 'block', marginBottom: 2 }}>Снять {formatMoney(preview.total)} ₽?</strong>Строк: {preview.lines.length}. Действие попадёт в журнал денег.</Notice>
              <Button variant="danger" size="lg" onClick={() => void apply()}>Да, отменить начисления</Button>
              <Button size="full" onClick={() => setConfirming(false)}>Назад</Button>
            </>
          ) : (
            <Button variant="primary" size="lg" disabled={!preview || preview.lines.length === 0 || !reason.trim()} onClick={() => setConfirming(true)}>Отменить начисления</Button>
          )}
        </div>
      </aside>
    </div>
  );
}
