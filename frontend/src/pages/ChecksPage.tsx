import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, useApi } from '@/api';
import type { CheckGroup, CheckRow } from '@/domain/types';
import { DataTable, Notice, SelectInput, type Column } from '@/ui';

const TONE_COLOR: Record<CheckGroup['severity'], string> = { error: 'var(--warn)', warn: 'var(--warn)', info: 'var(--ink-2)' };
const SEVERITY_LABEL: Record<CheckGroup['severity'], string> = { error: 'Ошибки', warn: 'Посмотреть', info: 'К сведению' };
const ORDER: Array<CheckGroup['severity']> = ['error', 'warn', 'info'];

export function ChecksPage() {
  const navigate = useNavigate();
  const [period, setPeriod] = useState<string | undefined>(undefined);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const { data, error } = useApi(() => api.checks.get(period), [period]);

  if (!data) return error ? <div className="pad"><Notice tone="warn">{error}</Notice></div> : null;

  const groups = data.groups;
  const firstWithRows = ORDER.flatMap((severity) => groups.filter((group) => group.severity === severity && group.rows.length > 0))[0];
  const selected = groups.find((group) => group.id === selectedId) ?? firstWithRows ?? groups[0];
  const errors = groups.filter((group) => group.severity === 'error').reduce((sum, group) => sum + group.rows.length, 0);
  const warnings = groups.filter((group) => group.severity === 'warn').reduce((sum, group) => sum + group.rows.length, 0);
  const columns: Column<CheckRow & { key: string }>[] = (selected?.columns ?? []).map((header, index) => ({ key: header, header, render: (row) => row.cells[index] }));
  const rows = (selected?.rows ?? []).map((row, index) => ({ ...row, key: String(index) }));

  return (
    <div className="split">
      <aside className="pane" style={{ width: 320 }} aria-label="Список сверок">
        <div className="bar" style={{ gap: 8 }}>
          <h1 className="title">Сверки</h1>
          <div style={{ flex: '1 1 140px', minWidth: 0 }}>
            <SelectInput aria-label="Месяц" value={data.period} onChange={(event) => { setPeriod(event.target.value); setSelectedId(null); }}>
              {data.months.map((month) => <option key={month.period} value={month.period}>{month.title}</option>)}
            </SelectInput>
          </div>
        </div>
        <div className="small muted" style={{ padding: '8px 16px', borderBottom: '1px solid var(--line)' }}>
          {errors === 0 && warnings === 0 ? 'Всё сходится.' : `Ошибок: ${errors}, посмотреть: ${warnings}`}
        </div>
        <div className="scroll" style={{ padding: '4px 8px 12px' }}>
          {ORDER.map((severity) => (
            <div key={severity}>
              <div className="field-label" style={{ padding: '10px 8px 4px' }}>{SEVERITY_LABEL[severity]}</div>
              {groups.filter((group) => group.severity === severity).map((group) => {
                const active = group.id === selected?.id;
                return (
                  <button key={group.id} type="button" aria-current={active ? 'true' : undefined} onClick={() => setSelectedId(group.id)}
                    style={{ display: 'flex', justifyContent: 'space-between', gap: 12, width: '100%', minHeight: 44, padding: '8px 10px', textAlign: 'left', border: 0, borderRadius: 6, fontWeight: 500, background: active ? 'var(--brand)' : 'transparent', color: active ? '#fff' : 'var(--ink)' }}>
                    <span>{group.title}</span>
                    <span className="mono strong" style={{ color: active ? '#fff' : group.rows.length > 0 ? TONE_COLOR[severity] : 'var(--ink-2)' }}>{group.rows.length}</span>
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      </aside>

      <main className="main">
        {selected && (
          <>
            <div className="bar" style={{ alignItems: 'flex-start', justifyContent: 'space-between' }}>
              <div style={{ flex: '1 1 360px', minWidth: 0 }}>
                <h2 style={{ fontSize: 18, fontWeight: 700 }}>{selected.title}</h2>
                <p className="small muted" style={{ marginTop: 3, lineHeight: 1.45 }}>{selected.description}</p>
              </div>
              <div className="row">
                {selected.action && selected.actionTo && <Link to={selected.actionTo} className="btn" style={{ width: 148 }}>{selected.action}</Link>}
                {selected.rows.length > 0 ? <a href={api.checks.csvUrl(selected.id, data.period)} className="btn" style={{ width: 148 }}>Выгрузить CSV</a> : null}
              </div>
            </div>
            <DataTable columns={columns} rows={rows} rowKey={(row) => row.key} minWidth={680}
              onRowClick={(row) => { if (row.abonentId) navigate(`/abonents?id=${encodeURIComponent(row.abonentId)}`); }}
              empty={<span className="muted">Расхождений нет за месяц: {data.title.toLowerCase()}.</span>} />
          </>
        )}
      </main>
    </div>
  );
}
