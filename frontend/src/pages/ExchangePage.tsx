import { useState, type ChangeEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { ExchangeBatch, ExchangeLine } from '@/domain/types';
import { Button, DataTable, Notice, SelectInput, type Column } from '@/ui';

const PARTNERS = ['Все', '1С', 'Сбербанк', 'Платосфера', 'Город', 'ГИС ЖКХ', 'Соцзащита', 'Показания'];
const STAGES = ['Файл', 'Столбцы', 'Проверка', 'Проведение'];

function stats(batch: ExchangeBatch) {
  const bad = batch.lines.filter((line) => line.state !== 'ok').length;
  return { bad, ok: batch.lines.length - bad };
}

function batchStatus(batch: ExchangeBatch): { text: string; className: string } {
  if (batch.posted) return { text: 'Проведено', className: 'ok' };
  const { bad } = stats(batch);
  return bad ? { text: `Строк с проблемами: ${bad}`, className: 'warn' } : { text: 'Готово к проведению', className: 'ok' };
}

export function ExchangePage() {
  const { data: batches, error: loadError } = useApi(() => api.exchange.batches(), []);
  const [filter, setFilter] = useState('Все');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const visible = (batches ?? []).filter((batch) => filter === 'Все' || batch.partner === filter);
  const batch = visible.find((item) => item.id === selectedId) ?? visible[0] ?? null;
  const { data: operations } = useApi(() => (batch ? api.exchange.operations(batch.partner) : Promise.resolve([])), [batch?.partner]);

  const onFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    setError(null);
    try {
      const created = await api.exchange.upload(file);
      setFilter('Все');
      setSelectedId(created.id);
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  const post = async (id: string) => {
    setError(null);
    try {
      await api.exchange.post(id);
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  const columns: Column<ExchangeLine>[] = batch
    ? [
        { key: 'line', header: 'Строка', render: (row) => row.line },
        { key: 'key', header: batch.keyColumn, mono: true, render: (row) => row.key },
        {
          key: 'state', header: 'Результат',
          render: (row) => row.state === 'ok'
            ? <span className="ok strong">{batch.posted ? 'Проведено' : 'Готово'}</span>
            : <span className="warn strong">{row.state === 'not-found' ? 'Счёт не найден, не будет проведён' : 'Повтор в файле, не будет проведён'}</span>,
        },
        { key: 'amount', header: 'Сумма, ₽', align: 'right', mono: true, render: (row) => formatMoney(row.amount) },
        { key: 'action', header: 'Действие', render: (row) => (row.state === 'ok' ? <Link to={`/abonents?q=${encodeURIComponent(row.key)}`}>Открыть</Link> : <span className="muted">—</span>) },
      ]
    : [];

  const currentStage = batch?.posted ? 3 : 2;
  const numbers = batch ? stats(batch) : null;

  return (
    <div className="split">
      <aside className="pane" style={{ width: 340 }}>
        <div className="stack" style={{ padding: 12, gap: 8, borderBottom: '1px solid var(--line)' }}>
          <label className="drop">
            Выберите файл для загрузки
            <span className="muted" style={{ fontSize: 12.5, fontWeight: 400, marginTop: 2 }}>CSV или TXT, строки «лицевой счёт;сумма»</span>
            <input type="file" accept=".csv,.txt" onChange={(event) => void onFile(event)} />
          </label>
          <div role="group" aria-label="Источник" style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 4 }}>
            {PARTNERS.map((partner) => (
              <button key={partner} type="button" aria-pressed={filter === partner} onClick={() => setFilter(partner)} style={{ height: 30, padding: '0 2px', borderRadius: 6, fontSize: 12.5, fontWeight: 500, border: `1px solid ${filter === partner ? 'var(--brand)' : 'var(--control)'}`, background: filter === partner ? 'var(--brand)' : 'var(--surface)', color: filter === partner ? '#fff' : 'var(--ink)' }}>{partner}</button>
            ))}
          </div>
        </div>
        <div className="scroll">
          {visible.map((item) => {
            const status = batchStatus(item);
            return (
              <button key={item.id} type="button" className="list-item" aria-pressed={batch?.id === item.id} onClick={() => setSelectedId(item.id)}>
                <span className="between"><span className="strong" style={{ fontSize: 13.5 }}>{item.partner}</span><span className="muted nowrap" style={{ fontSize: 12.5 }}>{item.when}</span></span>
                <span style={{ display: 'block', fontSize: 13, marginTop: 2 }}>{item.title}</span>
                <span className={status.className} style={{ display: 'block', fontSize: 12.5, fontWeight: 500, marginTop: 3 }}>{status.text}</span>
              </button>
            );
          })}
          {visible.length === 0 && <p className="pad muted">Загрузок от этого источника нет.</p>}
        </div>
      </aside>

      {batch ? (
        <main className="main">
          <header className="stack" style={{ padding: '12px 16px', gap: 8, borderBottom: '1px solid var(--line)' }}>
            <div className="between" style={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <div>
                <h1 className="title">{batch.title}</h1>
                <p className="small muted" style={{ marginTop: 2 }}><span className="mono">{batch.fileName}</span> · {batch.partner} · {batch.when}</p>
              </div>
              <label className="small muted row">Операции источника
                <SelectInput style={{ width: 280, minHeight: 38 }}>{operations?.map((operation) => <option key={operation}>{operation}</option>)}</SelectInput>
              </label>
            </div>
            <ol aria-label="Этапы" style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexWrap: 'wrap', gap: 4, fontSize: 13, fontWeight: 500 }}>
              {STAGES.map((stage, index) => (
                <li key={stage} aria-current={index === currentStage ? 'step' : undefined} style={{ padding: '5px 12px', borderRadius: 14, background: index === currentStage ? 'var(--brand)' : index < currentStage ? 'var(--ok-bg)' : 'var(--surface)', color: index === currentStage ? '#fff' : 'var(--ink)', border: `1px solid ${index === currentStage ? 'var(--brand)' : index < currentStage ? 'var(--ok-line)' : 'var(--control)'}` }}>{index + 1}. {stage}</li>
              ))}
            </ol>
            {(error ?? loadError) && <Notice tone="warn">{error ?? loadError}</Notice>}
          </header>
          <div className="bar bar-tint" style={{ gap: '6px 24px', fontSize: 13.5, padding: '8px 16px' }}>
            <span>Строк: <strong>{batch.total.toLocaleString('ru-RU')}</strong></span>
            <span>Готово: <strong>{numbers?.ok.toLocaleString('ru-RU')}</strong></span>
            <span className={numbers?.bad ? 'warn' : 'ok'}>С проблемами: <strong>{numbers?.bad}</strong></span>
            <span>Сумма: <strong className="mono">{batch.sum === null ? '—' : `${formatMoney(batch.sum)} ₽`}</strong></span>
          </div>
          <DataTable columns={columns} rows={batch.lines} rowKey={(row) => row.id} minWidth={680} rowClass={(row) => (row.state !== 'ok' ? 'is-warn' : undefined)} />
          <div className="bar bar-end">
            <Button size="auto" style={{ width: 220 }} variant="primary" disabled={batch.posted || numbers?.ok === 0} onClick={() => void post(batch.id)}>{batch.posted ? 'Уже проведено' : `Провести строк: ${numbers?.ok.toLocaleString('ru-RU')}`}</Button>
          </div>
        </main>
      ) : (
        <main className="main">
          {(error ?? loadError) && <div className="pad"><Notice tone="warn">{error ?? loadError}</Notice></div>}
          <div className="muted" style={{ margin: 'auto' }}>Выберите загрузку слева.</div>
        </main>
      )}
    </div>
  );
}
